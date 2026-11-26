using System.Net;
using System.Text;
using System.Text.Json;

namespace Wafish.Performance;

internal static class BenchmarkPreflight
{
    public const int MaximumBodyBytes = 4 * 1024 * 1024;
    public static async Task VerifyAsync(Uri endpoint, HttpMethod method, string path, string? body = null, bool knownWorkload = true, CancellationToken cancellationToken = default, BenchmarkTransport? transport = null)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false };
        transport?.ConfigureHandler(handler);
        using var client = new HttpClient(handler) { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(method, path);
        transport?.ConfigureRequest(request);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        transport?.VerifyResponse(response, upstream: knownWorkload);
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("Workload preflight did not return HTTP 200.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var received = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var length = await stream.ReadAsync(buffer, deadline.Token);
            if (length == 0) break;
            if (received.Length + length > MaximumBodyBytes) throw new InvalidOperationException("Workload preflight exceeded its body limit.");
            received.Write(buffer, 0, length);
        }
        if (!knownWorkload) return;
        var bytes = received.ToArray();
        var route = path.Split('?')[0];
        var valid = route switch
        {
            "/api/perf/plain" => bytes.AsSpan().SequenceEqual("OK"u8),
            "/api/perf/echo" => bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(body ?? "")),
            "/api/perf/json-small" => IsExpectedJson(bytes),
            _ when route.StartsWith("/api/perf/payload/", StringComparison.Ordinal) && int.TryParse(route.Split('/')[^1], out var size)
                => bytes.Length == Math.Clamp(size, 0, MaximumBodyBytes) && bytes.All(value => value == (byte)'x'),
            _ => throw new ArgumentException("No payload expectation exists for this managed workload.")
        };
        if (!valid) throw new InvalidOperationException("Workload preflight payload did not match the benchmark contract.");
    }
    private static bool IsExpectedJson(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            return json.RootElement.GetProperty("id").GetInt32() == 1 && json.RootElement.GetProperty("name").GetString() == "Wafish performance payload"
                && json.RootElement.GetProperty("values").EnumerateArray().Select(value => value.GetInt32()).SequenceEqual(Enumerable.Range(1, 32));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
    }
}
