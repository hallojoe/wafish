# SampleDiscoveryCatalog

This optional local catalog demonstrates the gateway's HTTP JSON service-discovery plugin.
It advertises only the two sample backend origins assigned by Aspire.
The default topology remains unchanged.

```bash
aspire start --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive -- --EnableDiscoveryDemo=true
aspire wait discovery-catalog --apphost Wafish.AppHost/Wafish.AppHost.csproj --non-interactive
```

GET `/catalog` returns an array of backend URLs. It starts with `backend-one` only.
The catalog supports no caller-supplied URLs or arbitrary instance identifiers.
Control requests require the private topology credential through `X-Catalog-Key`.
GET `/__catalog` reports membership, availability, and request count.
POST `/__catalog` accepts this bounded configuration:

```json
{"instances":["backend-one","backend-two"],"unavailable":false}
```

Use an empty instance list to remove all endpoints. Set `unavailable` to return HTTP 503 from `/catalog`.
The health endpoint remains healthy during deliberate catalog failures, so Aspire can keep the fixture running.
All listeners use loopback. Responses contain no credentials and disable caching.

Run the complete isolated scenario while the AppHost is stopped:

```bash
python3 scripts/check-service-discovery.py
```

The script creates a separate route and an actual `service-discovery` cluster binding.
It verifies endpoint addition, removal, stable destination IDs, last-known traffic, stale rejection, and recovery.
The binding uses five-second refreshes, a ten-second stale limit, and `RejectNewTraffic`.
Discovery IDs derive from normalized endpoint addresses. Reordering or restoring the same address retains its ID.
Changing an address creates a different destination identity.
The administration resource view reports failed and rejected bindings, then clears them after recovery.
The script removes its own temporary topology and generated credential parameter.
It keeps the default gRPC registration example available throughout the profile.

```bash
dotnet build Wafish.slnx -c Release
dotnet run --project tests/Wafish.Regression -c Release -- --group sample-discovery
```
