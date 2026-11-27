using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wafish.Dashboard.Infrastructure;
using Wafish.Dashboard.Modules.Dashboard.Services;

internal sealed partial class AdministrationRegressionSuite
{
    private async Task CheckAIRequestsAsync()
    {
        foreach (var (provider, parameter) in new[]
        {
            ("openai", "max_completion_tokens"),
            ("OpenAI", "max_completion_tokens"),
            ("deepseek", "max_tokens"),
            ("custom", "max_tokens")
        })
        {
            await Check($"AI requests: {provider} connection, completion, and streaming token limits", async () =>
            {
                var requests = new ConcurrentQueue<JsonElement>();
                var builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                await using var api = builder.Build();
                api.MapPost("/v1/chat/completions", async (HttpContext context) =>
                {
                    using var document = await JsonDocument.ParseAsync(context.Request.Body);
                    var body = document.RootElement.Clone();
                    requests.Enqueue(body);
                    var unwanted = parameter == "max_tokens" ? "max_completion_tokens" : "max_tokens";
                    if (!body.TryGetProperty(parameter, out _) || body.TryGetProperty(unwanted, out _))
                    {
                        context.Response.StatusCode = 400;
                        await context.Response.WriteAsJsonAsync(new { error = new { message = "Unsupported token limit parameter" } });
                        return;
                    }
                    Require(context.Request.Headers.Authorization == "Bearer synthetic-test-key", "AI authorization changed.");
                    if (body.TryGetProperty("stream", out var stream) && stream.GetBoolean())
                    {
                        context.Response.ContentType = "text/event-stream";
                        await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\ndata: [DONE]\n\n");
                    }
                    else
                    {
                        await context.Response.WriteAsJsonAsync(new { choices = new[] { new { message = new { content = "Hello" } } } });
                    }
                });
                await api.StartAsync();
                using var apiClient = Client(api);
                await using var host = await StartHost("ApiKey", enableAi: true, configure: services =>
                    services.Configure<AIOptions>(options =>
                    {
                        options.Enabled = true;
                        options.Provider = provider;
                        options.ApiKey = "synthetic-test-key";
                        options.BaseUrl = apiClient.BaseAddress + "v1";
                        options.ChatModel = "configured-model";
                        options.MaxTokens = 731;
                    }));
                var service = host.Services.GetRequiredService<AIService>();
                var connection = await service.TestConnectionAsync();
                Require(connection.ok, connection.message);
                var completion = await service.CompleteAsync("System prompt", "Hi", model: "override-model");
                Require(completion.ok && completion.content == "Hello", "Completion failed: " + completion.content);
                var events = new List<AIStreamEvent>();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await foreach (var item in service.StreamChatAsync(Guid.NewGuid().ToString("N"), "Hi", timeout.Token))
                    events.Add(item);
                Require(events.All(item => item.Type != "error"), "Streaming failed.");
                Require(events.Any(item => item.Type == "chunk" && item.Content == "Hello") && events.Last().Type == "done",
                    "Streaming content or completion event changed.");
                var sent = requests.ToArray();
                Require(sent.Length == 3, "AI requests were missing or retried unexpectedly.");
                Require(sent.Select(body => body.GetProperty(parameter).GetInt32()).SequenceEqual(new[] { 5, 731, 731 }),
                    "Connection or configured token limits changed.");
                Require(sent[1].GetProperty("model").GetString() == "override-model", "Completion ignored the model override.");
                Require(sent[2].GetProperty("model").GetString() == "configured-model" && sent[2].GetProperty("tools").GetArrayLength() > 0,
                    "Streaming model or tool definitions changed.");
                await host.StopAsync();
                await api.StopAsync();
            });
        }
        await CheckAIReasoningCompatibilityAsync();
    }
}
