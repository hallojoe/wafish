# Shared benchmark helpers

These sources are linked into benchmark executables and regression checks.
The server and forwarding helpers use ASP.NET Core and YARP; the other helpers use the BCL.
NativeYarp remains independent of Wafish packages.

`BoundedLatencyHistogram` retains a fixed array, independent of request count.
It reports upper bounds for exact nearest-rank percentiles after all writers complete.
Values above one microsecond have at most 1% relative bucket error, apart from floating-point rounding.
Smaller values have at most one microsecond absolute error.
Values above 24 hours and nonfinite or negative values fail explicitly; they are never silently clamped.
The measurement strategy has no sampling loss or random rank error.

`BenchmarkOptions` accepts explicit loopback origins and both argument-value forms.

## Matched transports (R43)

Select `--transport=http1|h2c|https1|https2` on the runner. The default remains `http1`.
Both proxy legs use the selected protocol with `RequestVersionExact`; downgrade fails preflight.
The backend reports its actual request protocol. Managed preflight checks both legs.
Health checks, manifests, functional checks, and measured requests use the same transport.

| Mode | Client and upstream protocol | Encryption |
|---|---|---|
| `http1` | HTTP/1.1 | None |
| `h2c` | HTTP/2 prior knowledge | None |
| `https1` | HTTP/1.1 | TLS |
| `https2` | HTTP/2 | TLS with ALPN |

Managed defaults use the selected scheme on ports 5300–5302.
Explicit address schemes must match the mode. Managed listeners remain loopback-only.
TLS versions and cipher suites follow platform defaults. Record the operating system when comparing runs.

For TLS, the runner creates a one-day self-signed certificate in its private temporary directory.
Its SANs include `localhost`, `127.0.0.1`, and `::1`.
Clients pin that certificate and reject name mismatches. System certificate stores remain unchanged.
Child hosts receive its PFX path through `WAFISH_BENCHMARK_CERTIFICATE`.
The runner removes the certificate with its private databases after stopping its children.
Standalone TLS hosts require that environment variable and a matching local certificate.
External HTTPS endpoints use normal system trust. Their upstream protocol remains unverified.

Build Release binaries, then compare the same modes and workload:

```bash
for transport in http1 h2c https1 https2; do
  dotnet run --project tests/Wafish.Performance/Runner -c Release --no-build -- \
    --transport="$transport" --modes=native,full --duration=10 --warmup=3 --repetitions=3
done
```

JSON reports record both transport legs. CSV rows retain their host configuration hash.
Keep workload, concurrency, telemetry, and machine settings unchanged across comparisons.
These local measurements do not establish production capacity.

`BenchmarkArrivalSchedule` uses fixed arrival times, bounded workers, and a bounded queue.
`BenchmarkStreamWorkload` defines payload and timing limits for synthetic streams.
`BenchmarkBodyMeasurement` reads bodies through a pooled buffer and records delivery observations.
`BenchmarkPluginMatrix` validates explicit combinations and supplies versioned benchmark presets.

`BenchmarkDestinations` validates one to eight loopback origins and a supported YARP balancing policy.
NativeYarp, EnhancedYarp, and Runner link the same source without adding Wafish dependencies to the native baseline.
See [multiple destinations](../Runner/README.md#multiple-destinations-r48) for options, limits, and report interpretation.
