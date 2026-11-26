# R52: Bounded cancellable streaming

The sample emits synthetic SSE events with consistent instance and trace identity.
Limits bound event count, interval, total scheduled delay, and simultaneous backend work.
Cancellation stops writes and delays. Completion logs contain only counts and completion status.

Focused checks use real HTTP servers and active response-cache, compression, and request-retry bindings.
They verify first-event delivery before completion, disconnect cancellation, capacity, and invalid input.
Repeated completed streams have distinct IDs. Responses remain uncompressed and uncached.
The real Aspire check verifies event framing and both backend identities before and after topology restart.

Verification commands:

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release -- --group sample-streaming
python3 scripts/check-topology.py
dotnet run --project tests/Wafish.Regression -c Release
```

See `focused.json`, `topology.log`, `regression.json`, and `changed-coverage.json` for results.
Coverage combines the Release regression host and the Debug Aspire sample hosts.

Changed executable C# coverage is 95.12% (39/41 lines), including the sample entry point.

The Release solution build and all 304 core regression cases passed.
