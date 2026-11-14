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

Historical evidence logs retain their original names and paths.
Evidence README links and executable verification scripts use current paths.
