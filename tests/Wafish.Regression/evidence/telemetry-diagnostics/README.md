# R53: Local telemetry configuration diagnostics

An opt-in Development endpoint reports the startup registrations from SampleServiceDefaults.
It accepts direct loopback requests and rejects forwarded or remote requests.
Its fixed response fields contain no exporter endpoints, headers, credentials, connection strings, or payloads.
The snapshot describes configuration. It does not claim collector connectivity or successful export.

Focused HTTP checks cover disabled and production hosts, exporter presence and absence, and startup snapshot stability.
They also check the exact response field whitelist, no-store policy, and local access restrictions.
The Release solution build passed with zero warnings and errors.
Changed executable C# coverage is 100% (19/19 lines).

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release -- --group sample-telemetry-diagnostics
dotnet run --project tests/Wafish.Regression -c Release
```

See `focused.json`, `regression.json`, and `changed-coverage.json`.
The full suite includes sample contracts and the `need-seven` registration and operations checks.

All 305 core regression cases passed.
