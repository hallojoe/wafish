# R49: optional network fault injection

The optional Aspire resource forwards sample traffic to `backend-one` on loopback.
Private controls inject at most 100 resets or delays, each delay bounded to five seconds.
Client cancellation stops delayed work. Control requests require the generated topology credential.
Default topology resources and routes remain unchanged.

The Release solution build passed with zero warnings and errors.
The focused HTTP checks cover bounds, private controls, concurrent counts, forwarding, and cancellation.
The real Aspire scenario uses isolated storage, assigned ports, and real plugin bindings.
An unretried reset fails. With retry enabled, the same reset succeeds after two proxy attempts.
Sustained resets open the circuit and stop forwarding. Clearing faults restores traffic after the recovery interval.
The recovered route also preserves the configured delay.

The scenario stops its own topology and removes its generated credential parameter.
No existing AppHost, database, or volume was stopped or removed.

Changed C# source coverage includes the AppHost wiring and executable entry point.
See `changed-coverage.json`, `focused.json`, `topology.log`, and `regression.json`.

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release -- --group sample-faults
python3 scripts/check-network-faults.py
```

All 302 core regression cases passed, including sample deployment and sample contracts.
