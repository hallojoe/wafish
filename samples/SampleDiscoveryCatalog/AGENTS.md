# SampleDiscoveryCatalog: agent notes

Follow the root instructions. Read [README.md](README.md) and [PLAN.md](../../PLAN.md) before changes.

Keep this fixture loopback-only. Admit only bounded, configured backend identifiers and origins.
Keep healthy empty discovery distinct from malformed or unavailable discovery.
Preserve atomic selection updates and copies of returned arrays. Do not add persistent data or arbitrary URL controls.

Verify changes from the repository root:

```bash
dotnet build samples/SampleDiscoveryCatalog/SampleDiscoveryCatalog.csproj -c Release
dotnet run --project tests/Wafish.Regression -c Release -- --group discovery-catalog
```

Run `scripts/check-service-discovery.py` for topology or discovery-policy changes, only when its target AppHost is stopped.
Keep changed executable-line coverage at least 80%. Update the README and HTTP examples with behavior changes.
