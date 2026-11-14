# Plugin resource lifecycle contract

Registration in DI does not start a resource.
A resource owner serializes lifecycle operations. Resources must tolerate repeated calls after completion.

| Operation | Required behavior |
|---|---|
| Start while running | Complete without starting duplicate work. |
| Successful start | Report running health and retain ownership of acquired handles. |
| Failed or cancelled partial start | Retain cleanup ownership until stop completes. Propagate caller cancellation. |
| Stop | Cancel or drain owned work, join it, and release acquired handles. |
| Stop after completed stop | Complete without repeating disposal. |
| Cancelled stop | Propagate cancellation; retain ownership for a later cleanup attempt. |
| Health query | Read state without starting work. Report faults without credentials. |

The runtime coordinator cleans up partial startup with an uncancelled stop call.
Cleanup failure is logged; resource ownership remains with the owner.
Shutdown disables further reconciliation, stops the background loop, and then stops resources in reverse order.
One stop failure does not skip peer cleanup. Repeated shutdown can finish a failed cleanup attempt.

These contracts cannot force an uncooperative plugin to release independently rooted threads or static references.
External domain disposal waits for active request leases before stopping plugin resources and disposing their provider.

## Compatibility checks

Run `python scripts/verify-packages.py` to compare public declarations with recorded baselines and compile isolated consumers.
Run `python tests/Wafish.PackageChecks/check-negative.py` to verify intentional break detection.
Build `tests/Wafish.PackageChecks` first when running the latter command alone.

Checks reject removed declarations, changed record constructors, missing assemblies, and new abstract requirements on existing contracts.
New default interface members and independent interface types remain compatible.
Baseline updates require an intentional API change and consumer migration notes.
These checks verify declared source and binary contracts; they do not prove behavioral compatibility for every external plugin.

The [reference checks](../tests/Wafish.Regression/PluginLifecycleContractChecks.cs) demonstrate repeat calls, partial startup, cancellation, and shutdown faults.
