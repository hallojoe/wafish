# Cancellable sample streaming

The sample endpoint sends newline-delimited JSON through the gateway:

```text
GET /api/local-service/stream?chunks=20&intervalMilliseconds=500
```

Each line contains `sequence`, `instance`, `traceId`, and a fixed synthetic `message`.
The existing `X-Backend-Instance` header identifies the backend.
The endpoint flushes each complete record. It does not build a complete response in memory.
`Cache-Control: no-store, no-transform` prevents caching and gateway compression.

| Parameter | Default | Bounds |
| --- | --- | --- |
| `chunks` | 5 | Integer, 1–100 |
| `intervalMilliseconds` | 250 | Integer, 0–1000 |

The first record has no scheduled delay. There is no delay after the final record.
The total scheduled delay, `(chunks - 1) × intervalMilliseconds`, cannot exceed 30 seconds.
Invalid values return HTTP 400 Problem Details before streaming starts.
Transport delays and slow clients can extend the elapsed duration.

## Read and cancel

Start the existing [Aspire topology](aspire.md). Use the gateway HTTP URL shown by `aspire describe`.
The registered sample route already includes the new endpoint.

```bash
export WAFISH_GATEWAY_URL=http://localhost:ASSIGNED_PORT
curl --no-buffer "$WAFISH_GATEWAY_URL/api/local-service/stream?chunks=20&intervalMilliseconds=500"
```

Replace `ASSIGNED_PORT` before running the command. Lines should appear before the request completes.
Press Ctrl+C after a few records. The backend stops producing records when cancellation reaches it.
In the backend logs, find `Sample stream ended after ... chunks; cancelled: True`.
Normal completion reports `cancelled: False`. Logs contain counts and cancellation state, without response payloads.

For automatic cancellation:

```bash
curl --no-buffer --max-time 2 "$WAFISH_GATEWAY_URL/api/local-service/stream?chunks=20&intervalMilliseconds=500"
```

Curl returns a timeout error because the response intentionally remains incomplete.
A server, proxy, or client can buffer data. HTTP chunks do not have to match JSON record boundaries.
Use a streaming reader and parse complete newline-delimited records.

## Gateway policy behavior

Use the route's plugin bindings to apply these configurations.
Prefer a dedicated exact-match streaming route when testing policies alongside other sample traffic.
Use the existing sample cluster and give the exact route precedence over its catch-all route.

| Plugin | Configuration | Expected behavior |
| --- | --- | --- |
| `response-cache` | `{"enabled":true,"fillWaitMilliseconds":0}` | Records pass through immediately. The response's `Cache-Control: no-store` prevents storage. |
| `compression` | `{"enabled":true,"minResponseSize":0,"mimeTypes":["application/x-ndjson"]}` | The backend no-transform directive prevents compression, even with explicit MIME selection and zero threshold. |
| `request-retry` | `{"enabled":true,"maxRetries":2,"timeoutSeconds":40,"totalTimeoutSeconds":45}` | A started response is not replayed. Deadlines still apply to the entire streaming operation. |

These bindings do not change production defaults. Remove test bindings or the dedicated test route afterward.

Request a compressed representation to verify that the backend directive takes precedence:

```bash
curl --compressed --no-buffer --include "$WAFISH_GATEWAY_URL/api/local-service/stream?chunks=20&intervalMilliseconds=500"
```

Expect incremental records without `Content-Encoding`.
Backend flushes do not necessarily become gateway codec flushes.
Live testing found that small compressed records could arrive only after the backend completed.
This sample uses `no-transform` to preserve early delivery through active compression bindings.
For applications that permit compression, configure the route with `flushAfterWrite: true` and `minResponseSize: 0`.
Include the streaming MIME type. This flushes each compressed gateway write without reconstructing backend record boundaries.
The sample retains no-transform, so its response remains uncompressed. See the [compression guide](../src/Wafish.Plugin.Compression/README.md#incremental-compressed-responses) for tradeoffs.

The cache forwards writes while retaining at most its configured capture allowance.
`no-store` prevents a completed stream from becoming a cache entry.
Avoid cache fill waiting on streaming routes: it can delay another request while the first request remains active.

Retries can apply to eligible failures before response transmission.
Once a response starts, retrying would duplicate records. Wafish aborts an expired stream instead.
For a demonstration, set both retry deadlines to one second and request 20 records at 500 milliseconds.
Expect initial records followed by an interrupted response. The backend records cancellation, with no replay.
A stream that needs reconnection must define its own cursor and deduplication contract.
This synthetic endpoint has no resume protocol or persistent data.

## Verification

```bash
dotnet run --project tests/Wafish.Regression -c Release -- --group sample-streaming
```

Checks cover validation, framing, identity, cache exclusion, both codec requests with no-transform, early delivery, disconnects, and retry deadlines.
The checks use real HTTP backends and active gateway plugin bindings.
See the [controller](../samples/SampleLocalService/Controllers/LocalServiceController.cs) and [regression checks](../tests/Wafish.Regression/SampleStreamingChecks.cs).
