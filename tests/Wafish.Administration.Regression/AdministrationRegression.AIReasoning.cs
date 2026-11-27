using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Modules.Dashboard.Services;
using Wafish.Dashboard.Modules.GatewayConfig.Services;

internal sealed partial class AdministrationRegressionSuite
{
    private const string ToolReasoningError = """
        {"error":{"message":"Function tools with reasoning_effort are not supported for gpt-6.1-terra in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'.","type":"invalid_request_error","param":"reasoning_effort","code":null}}
        """;

    private async Task CheckAIReasoningCompatibilityAsync()
    {
        foreach (var (effort, writeOnly, reject) in new[] { ("high", false, true), ("auto", false, true), ("high", true, true), ("high", false, false) })
        {
            await Check($"AI reasoning: {effort}, write-only={writeOnly}, reject={reject} preserves tool contracts", async () =>
            {
                var requests = new ConcurrentQueue<JsonElement>();
                await WithAIEndpointAsync("OpenAI", effort, async context =>
                {
                    using var document = await JsonDocument.ParseAsync(context.Request.Body);
                    var body = document.RootElement.Clone();
                    requests.Enqueue(body);
                    if (reject && requests.Count == 1)
                    {
                        context.Response.StatusCode = 400;
                        context.Response.ContentType = "application/json";
                        await context.Response.WriteAsync(ToolReasoningError);
                        return;
                    }
                    if (requests.Count == (reject ? 2 : 1))
                    {
                        Require(body.GetProperty("reasoning_effort").GetString() == (reject ? "none" : effort), "Unexpected reasoning on the tool request.");
                        var calls = new List<object>();
                        if (!writeOnly) calls.Add(new { index = 0, id = "read-1", type = "function", function = new { name = "list_routes", arguments = "{}" } });
                        calls.Add(new { index = 1, id = "write-1", type = "function", function = new
                        {
                            name = "create_route",
                            arguments = "{\"routeId\":\"unconfirmed-ai-route\",\"path\":\"/unconfirmed\",\"clusterId\":\"unconfirmed-ai-cluster\",\"destination\":\"http://127.0.0.1:9/\"}"
                        } });
                        context.Response.ContentType = "text/event-stream";
                        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(new
                        {
                            choices = new[] { new { delta = new { content = "Checking routes.", tool_calls = calls } } }
                        }) + "\n\ndata: [DONE]\n\n");
                        return;
                    }
                    Require(!body.TryGetProperty("tools", out _), "Follow-up request still exposes tools.");
                    Require(body.GetProperty("reasoning_effort").GetString() == (reject ? "none" : effort),
                        "Follow-up did not retain the effective reasoning for this exchange.");
                    var messages = body.GetProperty("messages").EnumerateArray().ToArray();
                    var assistant = Array.FindIndex(messages, item => item.TryGetProperty("tool_calls", out _));
                    Require(assistant >= 0 && messages[assistant].GetProperty("role").GetString() == "assistant", "Missing assistant tool-call context.");
                    Require(messages[assistant].GetProperty("content").GetString() == "Checking routes.", "Assistant preamble was lost.");
                    var ids = messages[assistant].GetProperty("tool_calls").EnumerateArray().Select(call => call.GetProperty("id").GetString()).ToArray();
                    var results = messages.Skip(assistant + 1).ToArray();
                    Require(results.Length == ids.Length && results.All(item => item.GetProperty("role").GetString() == "tool"), "Tool responses do not match the assistant calls.");
                    Require(ids.SequenceEqual(results.Select(item => item.GetProperty("tool_call_id").GetString())), "Tool-call IDs changed.");
                    Require(results.Single(item => item.GetProperty("tool_call_id").GetString() == "write-1").GetProperty("content").GetString()!.Contains("confirmation_required"),
                        "Pending write was reported as executed.");
                    context.Response.ContentType = "text/event-stream";
                    await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"Read complete; write needs confirmation.\"}}]}\n\ndata: [DONE]\n\n");
                }, async (service, host) =>
                {
                    var events = await CollectAIStreamAsync(service);
                    Require(events.All(item => item.Type != "error"), "Compatibility exchange failed: " + string.Join("; ", events.Select(item => item.Error)));
                    Require(events.Count(item => item.Type == "tool_call" && item.IsWriteTool) == 1, "Write confirmation event was lost or duplicated.");
                    Require(events.Count(item => item.Type == "tool_result") == (writeOnly ? 0 : 1), "Read tool ran more than once or a write ran automatically.");
                    Require(writeOnly || events.Any(item => item.Content == "Read complete; write needs confirmation."), "Text follow-up was lost.");
                    Require(requests.Count == (writeOnly ? 1 : 2) + (reject ? 1 : 0), "Unexpected request retry or write-only follow-up.");
                    var sent = requests.ToArray();
                    Require(effort == "auto" ? !sent[0].TryGetProperty("reasoning_effort", out _) : sent[0].GetProperty("reasoning_effort").GetString() == effort,
                        "Initial request ignored configured reasoning.");
                    if (reject)
                        foreach (var property in sent[0].EnumerateObject().Where(p => p.Name != "reasoning_effort"))
                            Require(property.Value.GetRawText() == sent[1].GetProperty(property.Name).GetRawText(), "Retry changed " + property.Name);
                    Require(host.Services.GetRequiredService<AIConfigStore>().Current.ReasoningEffort == effort, "Saved reasoning was changed.");
                    var config = JsonSerializer.Serialize(await host.Services.GetRequiredService<ConfigPersistenceService>().ExportFullConfigAsync());
                    Require(!config.Contains("unconfirmed-ai-route"), "Write tool bypassed user confirmation.");
                });
            });
        }

        foreach (var (name, provider, effort, status, error, expectedRequests) in new[]
        {
            ("repeated rejection", "openai", "high", 400, ToolReasoningError, 2),
            ("already none", "openai", "none", 400, ToolReasoningError, 1),
            ("other provider", "deepseek", "high", 400, ToolReasoningError, 1),
            ("rate limit", "openai", "high", 429, ToolReasoningError, 1),
            ("different parameter", "openai", "high", 400, ToolReasoningError.Replace("\"param\":\"reasoning_effort\"", "\"param\":\"temperature\""), 1),
            ("unrelated reasoning error", "openai", "high", 400, "{\"error\":{\"type\":\"invalid_request_error\",\"param\":\"reasoning_effort\",\"message\":\"Invalid effort\"}}", 1),
            ("malformed error", "openai", "high", 400, "upstream unavailable", 1),
            ("non-object error", "openai", "high", 400, "[]", 1),
            ("invalid error fields", "openai", "high", 400, "{\"error\":{\"type\":7}}", 1)
        })
        {
            await Check("AI reasoning: " + name + " remains bounded and visible", async () =>
            {
                var requests = 0;
                await WithAIEndpointAsync(provider, effort, async context =>
                {
                    Interlocked.Increment(ref requests);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(error);
                }, async (service, _) =>
                {
                    var events = await CollectAIStreamAsync(service);
                    Require(requests == expectedRequests, "Unexpected retry count: " + requests);
                    Require(events.Count(item => item.Type == "error") == 1 && events.Single(item => item.Type == "error").Error!.Contains("LLM API error " + status),
                        "Provider failure was hidden or replaced.");
                    Require(events.All(item => item.Type is not ("chunk" or "tool_call" or "tool_result")), "Failed request produced content or executed a tool.");
                });
            });
        }
    }

    private async Task WithAIEndpointAsync(string provider, string effort, RequestDelegate respond, Func<AIService, WebApplication, Task> verify)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var api = builder.Build();
        api.MapPost("/v1/chat/completions", respond);
        await api.StartAsync();
        using var client = Client(api);
        await using var host = await StartHost("ApiKey", enableAi: true, configure: services => services.Configure<AIOptions>(options =>
        {
            options.Enabled = true;
            options.Provider = provider;
            options.ReasoningEffort = effort;
            options.ApiKey = "synthetic-test-key";
            options.BaseUrl = client.BaseAddress + "v1";
            options.ChatModel = "gpt-6.1-terra";
            options.MaxTokens = 731;
        }));
        await verify(host.Services.GetRequiredService<AIService>(), host);
        await host.StopAsync();
        await api.StopAsync();
    }

    private static async Task<List<AIStreamEvent>> CollectAIStreamAsync(AIService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var events = new List<AIStreamEvent>();
        await foreach (var item in service.StreamChatAsync(Guid.NewGuid().ToString("N"), "List routes and propose a new route.", timeout.Token)) events.Add(item);
        return events;
    }
}
