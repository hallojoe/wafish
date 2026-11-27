using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wafish.Dashboard.Infrastructure;
using Wafish.Storage;
using Microsoft.Extensions.Options;

namespace Wafish.Dashboard.Modules.Dashboard.Services;

/// <summary>Event yielded by the streaming chat method.</summary>
public record AIStreamEvent
{
    public string Type { get; init; } = "";
    public string? Content { get; init; }
    public string? ToolName { get; init; }
    public string? ToolArgs { get; init; }
    public string? ToolCallId { get; init; }
    public bool IsWriteTool { get; init; }
    public string? ToolResult { get; init; }
    public double? ElapsedMs { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// OpenAI-compatible API integration with SSE streaming and Function Calling support.
/// </summary>
public class AIService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly AIConfigStore _config;
    private readonly AIFunctionService _functionService;
    private readonly IAIConversationRepository _conversationRepo;

    public AIService(AIConfigStore config, AIFunctionService functionService, IAIConversationRepository conversationRepo)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _config = config;
        _functionService = functionService;
        _conversationRepo = conversationRepo;
    }

    public bool IsConfigured => _config.IsConfigured;

    /// <summary>Test the LLM API connection with a minimal request.</summary>
    public async Task<(bool ok, string message)> TestConnectionAsync()
    {
        var opts = _config.Current;
        if (string.IsNullOrWhiteSpace(opts.ApiKey))
        {
            return (false, "API Key is not configured");
        }

        var baseUrl = opts.BaseUrl.TrimEnd('/');
        var body = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = opts.ChatModel,
            ["messages"] = new[] { new { role = "user", content = "Hi" } },
            [TokenLimitParameter(opts)] = 5
        }, JsonOpts);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                return (true, "Connection successful");
            }

            var errBody = await resp.Content.ReadAsStringAsync();
            return (false, $"HTTP {(int)resp.StatusCode}: {Truncate(errBody, 200)}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Non-streaming completion used by notification enhancement. Uses the configured
    /// chat model by default; callers can override <paramref name="model"/>.
    /// </summary>
    public async Task<(bool ok, string content)> CompleteAsync(
        string systemPrompt, string userMessage, string? model = null, CancellationToken ct = default)
    {
        var opts = _config.Current;
        if (!opts.IsConfigured)
        {
            return (false, "AI is not configured");
        }

        var baseUrl = opts.BaseUrl.TrimEnd('/');
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model ?? opts.ChatModel,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            [TokenLimitParameter(opts)] = opts.MaxTokens,
            ["temperature"] = opts.Temperature
        };
        var body = JsonSerializer.Serialize(payload, JsonOpts);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct);
                return (false, $"HTTP {(int)resp.StatusCode}: {Truncate(errBody, 300)}");
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");
                if (message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
                {
                    return (true, c.GetString()!.Trim());
                }
                // Some reasoning models put the answer in reasoning_content instead.
                if (message.TryGetProperty("reasoning_content", out var r) && r.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(r.GetString()))
                {
                    return (true, r.GetString()!.Trim());
                }
            }
            return (true, "");
        }
        catch (OperationCanceledException)
        {
            return (false, "Request was cancelled");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Stream a chat completion with Function Calling support.
    /// Yields content chunks, tool call/result events, and a final done event.
    /// </summary>
    public async IAsyncEnumerable<AIStreamEvent> StreamChatAsync(
        string sessionId,
        string userMessage,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var opts = _config.Current;
        if (!opts.IsConfigured)
        {
            yield return new AIStreamEvent { Type = "error", Error = "AI is not configured. Please set up API Key in Settings." };
            yield break;
        }

        // Load conversation history
        var history = await _conversationRepo.GetSessionMessagesAsync(sessionId, opts.MaxHistory, ct);

        // Build messages
        var messages = new List<Dictionary<string, object?>>();
        var systemPrompt = await _functionService.BuildSystemPromptAsync();
        messages.Add(new() { ["role"] = "system", ["content"] = systemPrompt });

        foreach (var entry in history)
        {
            messages.Add(new() { ["role"] = entry.Role, ["content"] = entry.Content });
        }

        // Add the current user message
        messages.Add(new() { ["role"] = "user", ["content"] = userMessage });

        // Save user message
        await _conversationRepo.SaveMessageAsync(new AIConversationEntry
        {
            SessionId = sessionId,
            Role = "user",
            Content = userMessage,
            CreatedAt = DateTime.Now
        }, ct);

        // Stream the chat with tools
        var fullResponse = new StringBuilder();
        var toolCallIds = new List<string>();
        var toolCallNames = new List<string>();
        var toolCallArgs = new List<string>();
        var eventCount = 0;

        await foreach (var evt in StreamLLMCallAsync(messages, AIFunctionService.GetToolDefinitions(), ct))
        {
            eventCount++;
            if (evt.Type == "chunk" && evt.Content != null)
            {
                fullResponse.Append(evt.Content);
            }

            if (evt.Type == "tool_call" && evt.ToolName != null)
            {
                toolCallIds.Add(evt.ToolCallId ?? $"call_{Guid.NewGuid():N}");
                toolCallNames.Add(evt.ToolName);
                toolCallArgs.Add(evt.ToolArgs ?? "{}");
            }
            yield return evt;
        }

        if (eventCount == 0)
        {
            yield return new AIStreamEvent { Type = "error", Error = "LLM API returned no streaming data. Check server logs for details (model name, base URL, API key validity)." };
        }

        // Build assistant message for history
        var assistantContent = fullResponse.ToString();
        var assistantMsg = new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = assistantContent };

        if (toolCallNames.Count > 0)
        {
            var toolCallsArray = toolCallNames.Select((name, i) => new Dictionary<string, object?>
            {
                ["id"] = toolCallIds[i],
                ["type"] = "function",
                ["function"] = new { name, arguments = toolCallArgs[i] }
            }).ToArray();
            assistantMsg["tool_calls"] = toolCallsArray;
        }

        messages.Add(assistantMsg);

        // Save assistant response
        await _conversationRepo.SaveMessageAsync(new AIConversationEntry
        {
            SessionId = sessionId,
            Role = "assistant",
            Content = assistantContent,
            FunctionCalls = toolCallNames.Count > 0 ? JsonSerializer.Serialize(toolCallNames) : null,
            CreatedAt = DateTime.Now
        }, ct);

        yield return new AIStreamEvent { Type = "done" };
    }

    /// <summary>Low-level LLM streaming call with tool handling.</summary>
    private async IAsyncEnumerable<AIStreamEvent> StreamLLMCallAsync(
        List<Dictionary<string, object?>> messages,
        List<object>? tools,
        [EnumeratorCancellation] CancellationToken ct,
        bool disableReasoning = false)
    {
        var opts = _config.Current;
        var baseUrl = opts.BaseUrl.TrimEnd('/');

        var requestPayload = new Dictionary<string, object?>
        {
            ["model"] = opts.ChatModel,
            ["messages"] = messages,
            ["stream"] = true,
            [TokenLimitParameter(opts)] = opts.MaxTokens,
            ["temperature"] = opts.Temperature
        };
        if (disableReasoning)
        {
            requestPayload["reasoning_effort"] = "none";
        }
        else if (!string.IsNullOrEmpty(opts.ReasoningEffort) && opts.ReasoningEffort != "auto")
        {
            requestPayload["reasoning_effort"] = opts.ReasoningEffort;
        }

        if (tools != null && tools.Count > 0)
        {
            requestPayload["tools"] = tools;
        }

        Stream? responseStream = null;
        HttpResponseMessage? response = null;
        string? errorMsg = null;
        int? statusCode = null;
        string? respContentType = null;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
                req.Content = new StringContent(JsonSerializer.Serialize(requestPayload, JsonOpts), Encoding.UTF8, "application/json");
                response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                statusCode = (int)response.StatusCode;
                respContentType = response.Content.Headers.ContentType?.MediaType;
                if (response.IsSuccessStatusCode)
                {
                    responseStream = await response.Content.ReadAsStreamAsync(ct);
                    break;
                }

                var errBody = await response.Content.ReadAsStringAsync(ct);
                if (attempt == 0 && statusCode == 400 && tools is { Count: > 0 }
                    && string.Equals(opts.Provider, "openai", StringComparison.OrdinalIgnoreCase)
                    && !Equals(requestPayload.GetValueOrDefault("reasoning_effort"), "none")
                    && IsToolReasoningRejection(errBody))
                {
                    response.Dispose();
                    response = null;
                    disableReasoning = true;
                    requestPayload["reasoning_effort"] = "none";
                    continue;
                }

                errorMsg = $"LLM API error {statusCode} ({respContentType}): {Truncate(errBody, 300)}";
                break;
            }
        }
        catch (OperationCanceledException)
        {
            errorMsg = "Request was cancelled. This may be due to client disconnect, HttpClient timeout, or HttpContext.RequestAborted.";
        }
        catch (Exception ex)
        {
            errorMsg = $"{ex.GetType().Name}: {ex.Message}";
        }

        using var responseLifetime = response;
        if (errorMsg != null)
        {
            yield return new AIStreamEvent { Type = "error", Error = errorMsg };
            yield break;
        }

        // Parse SSE stream
        using var reader = new StreamReader(responseStream!);
        var toolCallAccumulator = new Dictionary<int, (string id, string name, StringBuilder args)>();
        var hasToolCalls = false;
        var dataLineCount = 0;
        var assistantContent = new StringBuilder();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ") && !line.StartsWith("data:"))
            {
                continue;
            }

            dataLineCount++;

            // Extract data after "data: " or "data:"
            var data = line.StartsWith("data: ") ? line[6..] : line[5..];
            if (data.StartsWith(' '))
            {
                data = data[1..];
            }

            if (data == "[DONE]")
            {
                break;
            }

            ChatChunk? chunk;
            try { chunk = JsonSerializer.Deserialize<ChatChunk>(data, JsonOpts); }
            catch { continue; }
            if (chunk?.Choices == null || chunk.Choices.Count == 0)
            {
                continue;
            }

            var delta = chunk.Choices[0].Delta;
            if (delta == null)
            {
                continue;
            }

            // Stream content (some reasoning models put text in reasoning_content instead of content)
            if (!string.IsNullOrEmpty(delta.Content))
            {
                assistantContent.Append(delta.Content);
                yield return new AIStreamEvent { Type = "chunk", Content = delta.Content };
            }
            else if (!string.IsNullOrEmpty(delta.ReasoningContent))
            {
                // Reasoning models stream their thinking in reasoning_content; show separately
                yield return new AIStreamEvent { Type = "reasoning", Content = delta.ReasoningContent };
            }

            // Accumulate tool calls
            if (delta.ToolCalls != null)
            {
                hasToolCalls = true;
                foreach (var tc in delta.ToolCalls)
                {
                    var idx = tc.Index ?? 0;
                    if (!toolCallAccumulator.ContainsKey(idx))
                    {
                        toolCallAccumulator[idx] = (tc.Id ?? "", tc.Function?.Name ?? "", new StringBuilder());
                    }

                    var entry = toolCallAccumulator[idx];
                    if (!string.IsNullOrEmpty(tc.Id))
                    {
                        entry.id = tc.Id;
                    }

                    if (!string.IsNullOrEmpty(tc.Function?.Name))
                    {
                        entry.name = tc.Function.Name;
                    }

                    if (tc.Function?.Arguments != null)
                    {
                        entry.args.Append(tc.Function.Arguments);
                    }

                    toolCallAccumulator[idx] = entry;
                }
            }
        }

        if (dataLineCount == 0)
        {
            yield return new AIStreamEvent { Type = "error", Error = $"LLM API returned no SSE data lines. HTTP {statusCode} {respContentType}." };
            yield break;
        }

        // Execute accumulated tool calls
        if (hasToolCalls && toolCallAccumulator.Count > 0)
        {
            // Tool results must follow the assistant message that requested those calls.
            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = assistantContent.ToString(),
                ["tool_calls"] = toolCallAccumulator.OrderBy(x => x.Key).Select(x => new
                {
                    id = x.Value.id,
                    type = "function",
                    function = new { name = x.Value.name, arguments = x.Value.args.ToString() }
                }).ToArray()
            });
            var executedAnyReadTool = false;
            foreach (var kvp in toolCallAccumulator.OrderBy(x => x.Key))
            {
                var (id, name, args) = (kvp.Value.id, kvp.Value.name, kvp.Value.args.ToString());
                var isWrite = _functionService.IsWriteTool(name);

                yield return new AIStreamEvent
                {
                    Type = "tool_call",
                    ToolName = name,
                    ToolArgs = args,
                    ToolCallId = id,
                    IsWriteTool = isWrite
                };

                if (!isWrite)
                {
                    JsonElement? parsedArgs = null;
                    try { parsedArgs = JsonDocument.Parse(args).RootElement.Clone(); } catch { }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var result = await _functionService.ExecuteReadToolAsync(name, parsedArgs);
                    sw.Stop();
                    executedAnyReadTool = true;
                    yield return new AIStreamEvent
                    {
                        Type = "tool_result",
                        ToolName = name,
                        ToolResult = result,
                        ElapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1)
                    };
                    // Add tool result to messages for the next LLM call
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = id,
                        ["content"] = result
                    });
                }
                else
                {
                    // Mixed read/write batches still need a response for every call ID.
                    messages.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = id,
                        ["content"] = "{\"status\":\"confirmation_required\",\"message\":\"Not executed. Waiting for user confirmation.\"}"
                    });
                }
            }

            // If any read tools were executed, call LLM again for a streaming text response
            if (executedAnyReadTool)
            {
                // Remove tools for the second call to force a text response
                await foreach (var evt in StreamLLMCallAsync(messages, null, ct, disableReasoning))
                {
                    yield return evt;
                }
            }
        }
    }

    private static bool IsToolReasoningRejection(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object) return false;
            return error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() == "invalid_request_error"
                && error.TryGetProperty("param", out var param) && param.ValueKind == JsonValueKind.String
                && param.GetString() == "reasoning_effort"
                && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                && message.GetString()!.Contains("Function tools with reasoning_effort are not supported", StringComparison.OrdinalIgnoreCase)
                && message.GetString()!.Contains("reasoning_effort to 'none'", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    // OpenAI replaced max_tokens. Other compatible providers can still require the legacy field.
    private static string TokenLimitParameter(AIOptions options) =>
        string.Equals(options.Provider, "openai", StringComparison.OrdinalIgnoreCase)
            ? "max_completion_tokens"
            : "max_tokens";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    // JSON DTOs for parsing OpenAI SSE chunks
    private class ChatChunk
    {
        public string? Id { get; set; }
        public List<ChatChoice>? Choices { get; set; }
    }
    private class ChatChoice
    {
        public ChatDelta? Delta { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }
    private class ChatDelta
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("tool_calls")]
        public List<ToolCallDelta>? ToolCalls { get; set; }
    }
    private class ToolCallDelta
    {
        public int? Index { get; set; }
        public string? Id { get; set; }
        public ToolCallFunction? Function { get; set; }
    }
    private class ToolCallFunction
    {
        public string? Name { get; set; }
        public string? Arguments { get; set; }
    }
}
