---
name: wafish-benchmarks
description: Run or change Wafish versus Native YARP performance and streaming comparisons. Use for harness correctness, matched workloads, and report interpretation; not general application profiling.
---

# Benchmark Wafish

1. Read `tests/Wafish.Performance/Runner/README.md` and scoped agent notes.
   Inspect the current CLI options and report schema. Do not assume later task branches are present.
2. Build the complete Release solution before launching managed benchmark hosts.
   Run functional preflight before timed comparisons. Wrong status, payload, protocol, or backend identity invalidates a measurement.
3. Match Native YARP and Wafish workload, transport, backend configuration, request encoding, duration, concurrency, and warmup.
   Record effective configuration and report metadata. Do not attribute changes between incompatible runs to gateway overhead.
4. Keep scheduled workloads bounded. Reconcile offered, completed, dropped, and cancelled requests where supported.
   For streaming, distinguish first body bytes from completion. Client read boundaries are not packet or backend chunk counts.
   Keep cache state and plugin combinations explicit when comparing results.
5. Use isolated loopback resources for managed runs. Preserve user-owned Aspire resources during external-endpoint runs.
   The harness must clean up its own processes and temporary state after success, cancellation, or failure.
6. Run `dotnet run --project tests/Wafish.Regression -c Release --no-build -- --benchmarks` after harness changes.
   Follow broader checks required by scoped guidance. Preserve raw JSON and CSV alongside reviewed measurements.

## Interpretation

Report request failures, dropped work, noise, and measurement conditions alongside timing results.
Short correctness runs and instrumented coverage runs do not establish production capacity.
Use repeated comparable measurements before making performance claims or selecting regression budgets.
If no compatible comparison or budget command exists in this checkout, report that limit instead of inventing one.
Keep dated benchmark narratives in `ARCHIVE.md`, linked to retained raw artifacts.
