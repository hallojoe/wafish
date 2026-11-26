# R44: scheduled arrival verification

Verified on Linux with .NET SDK 10.0.403 on 2026-11-26.

- Release solution build passed with zero warnings and errors.
- All ten benchmark regression cases passed. See [results](benchmarks.json).
- A 100/s schedule against 100 ms work offers all 100 arrivals and reports queue drops.
- Active work stays within the worker limit. Completed, dropped, and cancelled counts reconcile.
- Unsaturated schedules dispatch every arrival. Cancellation drains queued work without dispatching it.
- A real delayed backend produces separate queue, service, and scheduled latency fields in all report formats.
- Existing transport and closed-loop comparisons still pass.

```bash
dotnet build Wafish.slnx -c Release -m:2
dotnet run --project tests/Wafish.Regression -c Release --no-build -- --benchmarks
```

Compatibility: the default remains closed-loop. No public package API changed.
Report schema version 3 adds arrival settings and per-run counts. CSV adds columns after existing columns.
The functional first suite remains closed-loop. These short tests do not establish throughput budgets.
