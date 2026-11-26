# R36: named local quota verification

Verified on Linux with .NET SDK 10.0.403 on 2026-11-26.

- Release solution build passed with zero warnings and errors.
- All 294 core regression cases passed. See [results](regression.json).
- Changed executable source coverage: 97.56% (40/41). See [coverage](changed-coverage.json).
- Focused local and Redis quota checks passed. See [results](focused.json).
- All 45 administration regression cases passed. See [results](administration.json).
- All 19 package API compatibility checks passed against unchanged reviewed snapshots. See [results](public-api.log).

Checks verify sharing across named routes for Global, Route, and IpAddress partitions.
Other groups, ungrouped routes, and client identities retain separate allowances.
Rejected requests do not reach the upstream delegate and retain retry guidance.
Conflicting group policies fail before binding storage and snapshot activation.
Authenticated HTTP checks verify previews, creates, updates, and batches return HTTP 400 with validation outcomes.
Previews use the complete candidate binding set and leave stored and active revisions unchanged.

```bash
dotnet build Wafish.slnx -c Release -m:2
dotnet run --project tests/Wafish.Regression -c Release --no-build
dotnet run --project tests/Wafish.Administration.Regression -c Release --no-build
```

Compatibility: an omitted or empty `quotaGroup` preserves independent local quotas.
The execution-plan property is additive. Reviewed API snapshots remain unchanged.
Grouping is local to one gateway process. It does not share counters across replicas.
