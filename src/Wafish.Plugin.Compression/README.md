# Wafish.Plugin.Compression

[README](README.md) · [Agent notes](AGENTS.md) · [Proposed work](../../PLAN.md) · [Project index](../../docs/projects.md)

## Intro

This plugin compresses eligible route responses with Gzip or Brotli. Its binding ID is `compression`.

```mermaid
flowchart LR
    Response["Upstream response"] --> Check{"Eligible response and accepted encoding?"}
    Check -->|"Yes"| Compress["Stream through Gzip or Brotli"]
    Check -->|"No"| Original["Keep original response"]
    Compress --> Client["Client response"]
    Original --> Client
```

## Real-world example

A reporting API returns large JSON documents. Clients that accept compression receive fewer response bytes.

## Real-world value

Large text responses can use less bandwidth while retaining streamed delivery.

## What it does

- Negotiate supported encodings from Accept-Encoding.
- Apply media type, size, and compression-level rules.
- Handle exclusions and response headers inside the proxy pipeline.

## What it does not do

- Compress every response regardless of status, range, or media type.
- Add new compression formats beyond the implemented encodings.

## Start here

Source project: [Wafish.Plugin.Compression.csproj](Wafish.Plugin.Compression.csproj).
Direct project references: [Wafish.Plugin.Abstractions](../Wafish.Plugin.Abstractions/README.md), [Wafish](../Wafish/README.md).

Build and verification commands are in [AGENTS.md](AGENTS.md). Future work is in [PLAN.md](../../PLAN.md).

## Minimum response size

Known-length responses use the configured `minResponseSize`, including values above 64 KiB.
Unknown-length responses use at most 64 KiB of prefix storage.
For unknown lengths, thresholds above 65,536 bytes bypass compression regardless of write chunk size.
At lower thresholds, reaching the threshold selects compression. Completing below it preserves the original response.
An early flush selects uncompressed output for unknown lengths, preserving prompt streaming.
The schema description exposes this fallback. Existing encoding, media-type, and HTTP exclusion rules remain effective.

[Verification](../../tests/Wafish.Regression/evidence/review111213/README.md).

## Encoding negotiation

Brotli and gzip compete by quality. Brotli wins equal positive qualities.
An explicit coding overrides the wildcard. Duplicate coding or wildcard qualities use the lowest value.
Unknown codings do not select a compressor.
Malformed lists, duplicate parameters, and invalid quality syntax bypass compression for the entire field.
Quality syntax follows [RFC 9110 section 12.4.2](https://httpwg.org/specs/rfc9110.html#quality.values): values range from zero to one with at most three decimal places.
Unsupported or unacceptable compression leaves the upstream representation unchanged; this middleware does not generate a 406 response.
Generated checks cover field order, multiple header lines, wildcards, duplicates, malformed values, `Vary`, and byte-for-byte decompression.

## Allocation measurements

The allocation check streams sixteen concurrent sixteen-MiB responses into non-buffering sinks.
Both compressed JSON and excluded binary responses deliver bytes before producers finish.
Measured managed allocations remain below sixteen MiB for the complete 256-MiB workload.
The focused run allocated approximately 233 KiB for JSON and 87 KiB for excluded binary.
Native codec state and process RSS are outside this managed-allocation measurement.
Existing bounded prefix buffering required no code change.

## Compression measurements (R40)

Subscribe to meter `Wafish.Plugin.Compression`, or use the sample service defaults:

```csharp
metrics.AddMeter("Wafish.Plugin.Compression");
```

| Counter | Unit | Meaning |
|---|---|---|
| `wafish.compression.input_bytes` | `By` | Body bytes accepted by the compression layer |
| `wafish.compression.output_bytes` | `By` | Body bytes accepted by its downstream stream |
| `wafish.compression.responses` | `{response}` | Completed or aborted middleware invocations |

Output includes compression headers and trailers. It excludes HTTP headers, transport framing, and TLS overhead.
Counters measure successful writes, not declared `Content-Length` values or confirmed client delivery.
A failed partial stream write can make the count a lower bound.
No response body is retained for measurement. Existing prefix-buffer limits remain unchanged.

Dimensions are `wafish.route.id`, `compression.encoding`, and `compression.outcome`.
Encodings are `br`, `gzip`, and `identity`; identity means this plugin passed the representation through.
Outcomes are `completed` and `aborted`. Disabled compression bindings emit no measurements.
Route admission retains at most 128 distinct configured route IDs per process lifetime.
Further routes use the reserved `__other__` label. Request paths, query values, users, and headers never become labels.
No route IDs are retained until a metric listener subscribes.

For completed responses with nonzero input, calculate savings as `100 * (1 - output_bytes / input_bytes)`.
Use matching route and encoding labels. Negative savings expose compression overhead on small or incompressible bodies.
Keep aborted responses separate; their partial byte counts do not establish completed-response savings.

## Route encoding policy (R41)

Use `allowedEncodings` to restrict a route to Brotli (`br`), Gzip (`gzip`), or neither.
An omitted list permits both algorithms. An empty list passes the upstream representation through unchanged.
Only allowed algorithms participate in quality negotiation. Disabled algorithms never override the configured policy.
Existing wildcard, duplicate-quality, malformed-header, identity-fallback, and `Vary` behavior remains unchanged.

```json
{
  "enabled": true,
  "allowedEncodings": ["br", "gzip"],
  "compressionLevel": "Optimal",
  "brotliCompressionLevel": "Fastest",
  "gzipCompressionLevel": "Optimal",
  "minResponseSize": 1024
}
```

`brotliCompressionLevel` and `gzipCompressionLevel` override the shared level for their algorithm.
Each accepts `Optimal`, `Fastest`, or `NoCompression`.
An omitted override uses the existing `compressionLevel`, which still defaults to `Optimal`.
Codec levels retain their .NET meanings; they do not promise equal compression ratios across algorithms.
Unknown encodings, duplicate entries, and invalid levels fail schema validation.
