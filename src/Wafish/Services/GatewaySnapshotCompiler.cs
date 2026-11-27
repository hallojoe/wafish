using System.Collections.Immutable;
using Wafish.Storage;
using Wafish.Storage.Entities;
using Yarp.ReverseProxy.Configuration;

namespace Wafish.Services;

/// <summary>Default compiler that captures enabled bindings without activating plugin modules.</summary>
public sealed class GatewaySnapshotCompiler : IGatewaySnapshotCompiler
{
    private readonly IPluginConfigurationRepository _plugins;
    private readonly IPluginActivationState _activationState;
    private readonly IReadOnlyList<IRoutePluginCompiler> _routeCompilers;
    private readonly IReadOnlyList<IClusterPluginCompiler> _clusterCompilers;
    private readonly IRouteRepository? _routes;
    private readonly IClusterRepository? _clusters;

    public GatewaySnapshotCompiler(
        IPluginConfigurationRepository plugins,
        IPluginActivationState? activationState = null,
        IEnumerable<IRoutePluginCompiler>? routeCompilers = null,
        IEnumerable<IClusterPluginCompiler>? clusterCompilers = null,
        IRouteRepository? routes = null,
        IClusterRepository? clusters = null)
    {
        _plugins = plugins;
        _activationState = activationState ?? new AllowAllPluginActivationState();
        _routeCompilers = BuildCompilerPipeline(routeCompilers, static compiler => compiler.Order);
        _clusterCompilers = BuildCompilerPipeline(clusterCompilers, static compiler => compiler.Order);
        _routes = routes;
        _clusters = clusters;
    }

    public async Task<GatewaySnapshot> CompileAsync(
        IReadOnlyList<RouteConfig> routes,
        IReadOnlyList<ClusterConfig> clusters,
        long version,
        CancellationToken ct = default,
        IReadOnlyList<PluginBindingEntity>? candidateBindings = null,
        IReadOnlyDictionary<string, string>? candidateRouteIds = null,
        IReadOnlyDictionary<string, string>? candidateClusterIds = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(clusters);
        ArgumentOutOfRangeException.ThrowIfNegative(version);

        clusters = clusters.Select(c => c with { Destinations = c.Destinations?.Where(d => !DynamicYarpConfigService.IsDraining(d.Value)).ToDictionary(d => d.Key, d => d.Value) }).ToArray();
        var routeIds = routes.Select(x => x.RouteId).ToHashSet(StringComparer.Ordinal);
        var clusterIds = clusters.Select(x => x.ClusterId).ToHashSet(StringComparer.Ordinal);
        var routeUidMap = candidateRouteIds ?? (_routes is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : (await _routes.GetAllRoutesAsync(ct)).Where(x => !string.IsNullOrWhiteSpace(x.RouteUid))
                .ToDictionary(x => x.RouteUid, x => x.RouteId, StringComparer.Ordinal));
        var clusterUidMap = candidateClusterIds ?? (_clusters is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : (await _clusters.GetAllClustersAsync(ct)).Where(x => !string.IsNullOrWhiteSpace(x.ClusterUid))
                .ToDictionary(x => x.ClusterUid, x => x.ClusterId, StringComparer.Ordinal));
        var routeUidsById = routeUidMap.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        var sourceBindings = candidateBindings ?? await _plugins.GetBindingsAsync(ct);
        var endpointBindings = sourceBindings.Where(b => b.Enabled && (b.PluginId.Equals("redirect", StringComparison.OrdinalIgnoreCase) || b.PluginId.Equals("claims-transform", StringComparison.OrdinalIgnoreCase) || b.PluginId.Equals("route-authorization", StringComparison.OrdinalIgnoreCase) || b.PluginId.Equals("request-aggregation", StringComparison.OrdinalIgnoreCase)))
            .Select(b => ToSnapshot(b, routeUidMap, clusterUidMap)).Where(b => b.Scope == PluginBindingScope.Route && routeIds.Contains(b.ScopeId));
        if (endpointBindings.GroupBy(b => (b.ScopeId, PluginId: b.PluginId.ToLowerInvariant())).Any(g => g.Count() > 1))
            throw new ArgumentException("A route can have only one enabled binding per endpoint policy.");
        var drafts = new List<CompiledRoutePlugin>();
        // Endpoint policies validate drafts before persistence, including when the plugin is disabled.
        foreach (var draft in sourceBindings.Where(b => (b.PluginId.Equals("redirect", StringComparison.OrdinalIgnoreCase) || b.PluginId.Equals("claims-transform", StringComparison.OrdinalIgnoreCase) || b.PluginId.Equals("route-authorization", StringComparison.OrdinalIgnoreCase) || b.PluginId.Equals("request-aggregation", StringComparison.OrdinalIgnoreCase)) &&
                     (!b.Enabled || !_activationState.IsPluginEnabled(b.PluginId))))
        {
            var binding = ToSnapshot(draft, routeUidMap, clusterUidMap);
            var route = routes.FirstOrDefault(r => r.RouteId == binding.ScopeId);
            if (route is not null)
                foreach (var compiler in _routeCompilers.Where(c => c.CanCompile(binding.PluginId))) drafts.Add(compiler.Compile(binding, route));
        }
        var bindings = sourceBindings
            .Where(x => x.Enabled)
            .Where(x => _activationState.IsPluginEnabled(x.PluginId))
            .Select(x => ToSnapshot(x, routeUidMap, clusterUidMap))
            .Where(x => x.Scope == PluginBindingScope.Route ? routeIds.Contains(x.ScopeId) : clusterIds.Contains(x.ScopeId))
            .ToArray();

        var routeBindings = Group(bindings, PluginBindingScope.Route);
        var clusterBindings = Group(bindings, PluginBindingScope.Cluster);
        var compiledRoutes = ImmutableArray.CreateBuilder<RouteConfig>(routes.Count);
        var routePlans = ImmutableDictionary.CreateBuilder<string, RouteExecutionPlan>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            // Published routes may contain derived metadata from a previous compilation.
            var metadata = route.Metadata?.Where(x => x.Key != "wafish:claims-transform" && x.Key != "wafish:route-authorization" && x.Key != "wafish:request-aggregation")
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            var current = route with { Metadata = metadata };
            var compiledPlugins = new List<CompiledRoutePlugin>();
            if (routeBindings.TryGetValue(route.RouteId, out var targetBindings))
            {
                foreach (var binding in targetBindings)
                {
                    foreach (var compiler in _routeCompilers.Where(x => x.CanCompile(binding.PluginId)))
                    {
                        var compiled = compiler.Compile(binding, current);
                        current = compiled.Route;
                        compiledPlugins.Add(compiled);
                    }
                }
            }
            compiledRoutes.Add(current);
            routePlans[route.RouteId] = new RouteExecutionPlan(route.RouteId, PluginExecutionPlanOrdering.Order(compiledPlugins));
        }

        var compiledClusters = ImmutableArray.CreateBuilder<ClusterConfig>(clusters.Count);
        var clusterPlans = ImmutableDictionary.CreateBuilder<string, ClusterExecutionPlan>(StringComparer.Ordinal);
        foreach (var cluster in clusters)
        {
            var current = cluster;
            var compiledPlugins = new List<CompiledClusterPlugin>();
            if (clusterBindings.TryGetValue(cluster.ClusterId, out var targetBindings))
            {
                foreach (var binding in targetBindings)
                {
                    foreach (var compiler in _clusterCompilers.Where(x => x.CanCompile(binding.PluginId)))
                    {
                        var compiled = compiler.Compile(binding, current);
                        current = compiled.Cluster;
                        compiledPlugins.Add(compiled);
                    }
                }
            }
            compiledClusters.Add(current);
            clusterPlans[cluster.ClusterId] = new ClusterExecutionPlan(cluster.ClusterId, PluginExecutionPlanOrdering.Order(compiledPlugins));
        }

        var snapshot = new GatewaySnapshot(
            version,
            DateTimeOffset.UtcNow,
            compiledRoutes.MoveToImmutable(),
            compiledClusters.MoveToImmutable(),
            routeBindings,
            clusterBindings,
            routePlans.ToImmutable(),
            clusterPlans.ToImmutable())
        { RouteUidsById = routes.ToImmutableDictionary(r => r.RouteId, r => routeUidsById.GetValueOrDefault(r.RouteId, r.RouteId), StringComparer.Ordinal) };
        foreach (var validator in _routeCompilers.OfType<IGatewaySnapshotValidator>())
        {
            validator.Validate(snapshot);
            foreach (var draft in drafts)
            {
                var plan = snapshot.RouteExecutionPlans[draft.Binding.ScopeId];
                var draftPlan = new RouteExecutionPlan(plan.RouteId, plan.Plugins.Where(p => !p.Binding.PluginId.Equals(draft.Binding.PluginId, StringComparison.OrdinalIgnoreCase)).Append(draft).ToImmutableArray());
                validator.Validate(snapshot with { RouteExecutionPlans = snapshot.RouteExecutionPlans.SetItem(plan.RouteId, draftPlan) });
            }
        }
        return snapshot;
    }

    private static PluginBindingSnapshot ToSnapshot(
        PluginBindingEntity binding,
        IReadOnlyDictionary<string, string> routeUidMap,
        IReadOnlyDictionary<string, string> clusterUidMap)
    {
        var currentScopeId = binding.Scope switch
        {
            PluginBindingScope.Route when !string.IsNullOrWhiteSpace(binding.RouteUid)
                && routeUidMap.TryGetValue(binding.RouteUid, out var routeId) => routeId,
            PluginBindingScope.Cluster when !string.IsNullOrWhiteSpace(binding.ClusterUid)
                && clusterUidMap.TryGetValue(binding.ClusterUid, out var clusterId) => clusterId,
            _ => binding.ScopeId
        };
        return new PluginBindingSnapshot(binding.Id, binding.PluginId, binding.Scope, currentScopeId,
            binding.ConfigJson, binding.SchemaVersion, binding.ConfigVersion, binding.Order);
    }

    private static TCompiler[] BuildCompilerPipeline<TCompiler>(
        IEnumerable<TCompiler>? compilers,
        Func<TCompiler, int> getOrder)
        where TCompiler : class
    {
        var registered = compilers?.ToArray() ?? [];

        return registered
            .Select((compiler, registrationIndex) => (compiler, registrationIndex))
            .OrderBy(item => getOrder(item.compiler))
            .ThenBy(item => item.registrationIndex)
            .Select(item => item.compiler)
            .ToArray();
    }

    private static ImmutableDictionary<string, ImmutableArray<PluginBindingSnapshot>> Group(
        IEnumerable<PluginBindingSnapshot> bindings, PluginBindingScope scope)
        => bindings.Where(x => x.Scope == scope)
            .GroupBy(x => x.ScopeId, StringComparer.Ordinal)
            .ToImmutableDictionary(
                x => x.Key,
                x => x.OrderBy(y => y.Order).ThenBy(y => y.PluginId, StringComparer.Ordinal).ToImmutableArray(),
                StringComparer.Ordinal);
}

/// <summary>Thread-safe in-memory publication point for compiled snapshots.</summary>
public sealed class GatewaySnapshotPublisher : IGatewaySnapshotPublisher
{
    private GatewaySnapshot _current = GatewaySnapshot.Empty;
    public GatewaySnapshot Current => Volatile.Read(ref _current);

    public GatewaySnapshot Publish(GatewaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        GatewaySnapshot observed;
        do
        {
            observed = Current;
            if (snapshot.Version <= observed.Version)
            {
                throw new InvalidOperationException($"Snapshot version {snapshot.Version} must be greater than current version {observed.Version}.");
            }
        }
        while (!ReferenceEquals(Interlocked.CompareExchange(ref _current, snapshot, observed), observed));
        return snapshot;
    }
}
