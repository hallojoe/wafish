# R51: HTTP JSON service-discovery demonstration

The opt-in Aspire catalog advertises only two configured loopback backends.
Private controls select membership or return a deliberate HTTP 503.
The default topology and gRPC registration example remain unchanged.

The Release solution build passed with zero warnings and errors.
Focused HTTP checks verify private controls, input bounds, endpoint membership, failures, and recovery.
The real Aspire scenario creates a separate route and an actual HTTP JSON service-discovery binding.
It verifies stable destination IDs after addition, removal, and restoration of the same addresses.
Last-known traffic continues before stale expiry. Expired discovery returns HTTP 503 under `RejectNewTraffic`.
Resource statistics expose failed and rejected bindings, then clear them after recovery.
All resources use assigned ports and isolated storage. The script cleans up its own topology and credential parameter.

Changed C# source coverage includes AppHost wiring and the executable entry point.
See `changed-coverage.json`, `focused.json`, `topology.log`, and `regression.json`.

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release -- --group sample-discovery
python3 scripts/check-service-discovery.py
```

All 303 core regression cases passed, including sample deployment and sample contracts.
