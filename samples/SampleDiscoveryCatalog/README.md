# SampleDiscoveryCatalog

This local fixture publishes an HTTP JSON catalog for the existing sample backends.
It lets operators change membership and simulate registry failures without editing gateway destinations.

Start it through the [optional discovery profile](../../docs/service-discovery-sample.md):

```bash
aspire start --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive -- --EnableServiceDiscovery=true
```

Aspire assigns its loopback HTTP port and supplies the two backend origins.
The default topology does not start this resource. Existing gRPC registration remains independent.

| Endpoint | Purpose |
| --- | --- |
| `GET /catalog` | Return `{"endpoints":["http://..."]}` for the selected backends. |
| `GET /control` | Read the current mode and backend identifiers. |
| `PUT /control` | Select configured backends and a synthetic response mode. |
| `GET /openapi/v1.json` | Describe the catalog and control API. |

Modes are `healthy`, `unavailable` (HTTP 503), and `malformed` (invalid JSON).
An empty healthy selection represents successful discovery with no destinations.
Controls accept only startup-configured identifiers. They cannot introduce arbitrary upstream URLs.
Selection updates are atomic. Returned arrays do not expose mutable internal state.

Startup accepts one to eight distinct loopback HTTP origins in `Catalog:Backends`.
Names contain lowercase letters, digits, and hyphens, start with a letter, and contain at most 40 characters.
Origins cannot contain credentials, paths, query strings, or fragments.
`CATALOG_HTTP_PORT` selects the loopback listener. Request bodies are limited to 4 KiB.
The catalog stores no persistent state. Restart restores all configured backends in healthy mode.

This unauthenticated control fixture is for local development. Keep `/control` off gateway proxy routes.
Use the [HTTP examples](SampleDiscoveryCatalog.http), [implementation](DiscoveryCatalog.cs), and [walkthrough](../../docs/service-discovery-sample.md).

Verification:

```bash
dotnet build samples/SampleDiscoveryCatalog/SampleDiscoveryCatalog.csproj -c Release
dotnet run --project tests/Wafish.Regression -c Release -- --group discovery-catalog
python3 scripts/check-service-discovery.py
```

The Python check requires the target AppHost to be stopped. It creates and cleans up its own isolated topology.
