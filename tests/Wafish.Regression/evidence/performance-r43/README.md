# R43: matched transport verification

Verified on Linux with .NET SDK 10.0.403 on 2026-11-26.

- Release solution build passed with zero warnings and errors.
- All nine benchmark regression cases passed. See [results](benchmarks.json).
- Native YARP and Wafish passed matching HTTP/1.1, h2c, HTTP/1.1 TLS, and HTTP/2 TLS runs.
- Managed preflight checks the actual client and upstream protocol.
- Certificate tests reject unknown certificates, name mismatches, and missing certificates.
- Existing eight-mode comparisons, external runs, fault fixtures, and owned-directory cleanup still pass.

```bash
dotnet build Wafish.slnx -c Release -m:2
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --benchmarks
```

Compatibility: HTTP/1.1 cleartext remains the default. No public package API changed.
Report schema version 2 adds explicit transport metadata. External upstream protocols remain unverified.
These short runs establish correctness, not throughput claims. TLS settings follow platform defaults.
