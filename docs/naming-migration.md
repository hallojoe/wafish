# Wafish naming migration

Use `Wafish` for namespaces, types, extension methods, projects, assemblies, and package references.
Update project paths and deployment scripts on case-sensitive filesystems.
For example, call `AddWafishRuntime`, `MapWafishAdministration`, and `MapWafishProxy`.
Use `WafishProxyConfigProvider` when registering or inspecting the YARP provider.
The solution is now `Wafish.slnx`.

Rebuild consumers and external plugins against the renamed assemblies.
Update assembly-qualified reflection strings and embedded resource names.
NuGet package identifiers remain case-insensitive, but CLR type and assembly references require rebuilding.
Remove stale project `bin` and `obj` directories before the first build after migration.

The dashboard JWT issuer is now `Wafish.Dashboard`. Sign in again after upgrading.
MCP service names and webhook source labels use the same spelling.
Protobuf RPC names, field numbers, and the `wafish.v1` wire package remain unchanged.
Gateway configuration keys and built-in plugin identifiers remain unchanged.

## Repository branding and configured names

The sample administration API prefix is now `/wafish`. The React workspace remains at `/console`.
Update bookmarks, API clients, reverse-proxy rules, and frontend development proxies that use the previous sample prefix.
Explicit host overrides remain supported. The library's `apigateway` default is unchanged.

The Redis rate-limit key default is `wafish:rl`. The sample Redis prefix is `wafish:`.
Changing a prefix starts a separate counter namespace. Existing counters expire under their previous keys.
Coordinate the prefix change across gateway instances when they share rate limits.
Live Redis checks now read `WAFISH_REDIS_SERVER`.

Registration ownership metadata uses `wafish.registration.service` and `wafish.registration.instance`.
Stop registered clients cleanly before upgrading so their previous registrations are removed.
For retained configuration exports, rename the previous ownership keys before importing, then register clients again.
Keep service IDs, instance IDs, and user-defined routes unchanged.

Archived text reports now use Wafish branding. Their measurements and outcomes have not changed.
Evidence README links and executable verification scripts use current paths.
