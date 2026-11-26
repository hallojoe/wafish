# R42: paced streaming verification

Verified on Linux with .NET SDK 10.0.403 on 2026-11-26.

- Release solution build passed with zero warnings and errors.
- All eleven benchmark regression cases passed. See [results](benchmarks.json).
- Chunk and event-stream endpoints deliver their first bytes while backend work remains active.
- Client disconnects stop producers and increment cancellation counters.
- Invalid output and timing bounds return HTTP 400. Event framing and complete payloads pass preflight.
- Native YARP and Wafish reports distinguish first body bytes from completion and retain exact byte counts.
- Existing scheduled arrival, transport, and closed-loop checks still pass.

```bash
dotnet build Wafish.slnx -c Release -m:2
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --benchmarks
```

Compatibility: baseline workloads remain the default. No public package API changed.
Report schema version 4 adds stream settings and delivery observations. CSV appends new columns.
Read boundaries are client observations, not backend chunk or packet counts.
These short runs establish correctness, not production buffering or throughput guarantees.
