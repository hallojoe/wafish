using System.Collections.Concurrent;
using Wafish.Infrastructure.Performance;
using Wafish.Models;
using Wafish.Services;
using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using Yarp.ReverseProxy.Model;

namespace Wafish.Plugin.Metrics;

public enum PluginMetricOutcome { Completed, Faulted, Cancelled }

public sealed record PluginMetricSnapshot(long Requests, long Errors, long RequestBytes, long ResponseBytes, long DurationMicroseconds)
{
    public long Exceptions { get; init; }
    public long Cancellations { get; init; }
}

public sealed class PluginMetricStore : IDisposable
{
    private readonly System.Diagnostics.Metrics.Meter _meter = new("Wafish.Plugin.Metrics", "1.0");
    private readonly object _gate = new();
    private readonly IGatewaySnapshotPublisher? _snapshots;
    private long _version = -1;
    private HashSet<string>? _routes, _clusters, _destinations;
    public const int MaximumSeriesPerScope = 10_000;
    public PluginMetricStore() => RegisterInstruments();
    public PluginMetricStore(IGatewaySnapshotPublisher snapshots) : this() => _snapshots = snapshots;

    private void RegisterInstruments()
    {
        foreach (var scope in new[] { "route", "cluster" })
        {
            IReadOnlyDictionary<string, PluginMetricSnapshot> Read() => scope == "route" ? GetRoutes() : GetClusters();
            IEnumerable<System.Diagnostics.Metrics.Measurement<long>> Observe(Func<PluginMetricSnapshot, long> value)
                => Read().Select(p => new System.Diagnostics.Metrics.Measurement<long>(value(p.Value), new KeyValuePair<string, object?>("wafish." + scope + ".id", p.Key)));
            _meter.CreateObservableCounter("wafish." + scope + ".requests", () => Observe(s => s.Requests), "{request}");
            _meter.CreateObservableCounter("wafish." + scope + ".errors", () => Observe(s => s.Errors), "{request}");
            _meter.CreateObservableCounter("wafish." + scope + ".exceptions", () => Observe(s => s.Exceptions), "{request}");
            _meter.CreateObservableCounter("wafish." + scope + ".cancellations", () => Observe(s => s.Cancellations), "{request}");
            _meter.CreateObservableCounter("wafish." + scope + ".request_bytes", () => Observe(s => s.RequestBytes), "By");
            _meter.CreateObservableCounter("wafish." + scope + ".response_bytes", () => Observe(s => s.ResponseBytes), "By");
            _meter.CreateObservableCounter("wafish." + scope + ".duration", () => Read().Select(p =>
                new System.Diagnostics.Metrics.Measurement<double>(p.Value.DurationMicroseconds / 1_000_000d,
                    new KeyValuePair<string, object?>("wafish." + scope + ".id", p.Key))), "s");
        }
    }
    public void Dispose() => _meter.Dispose();

    private void Refresh()
    {
        if (_snapshots is null || _snapshots.Current.Version == _version)
        {
            return;
        }

        var snapshot = _snapshots.Current;
        _routes = snapshot.Routes.Select(r => r.RouteId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _clusters = snapshot.Clusters.Select(c => c.ClusterId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _destinations = snapshot.Clusters.SelectMany(c => (c.Destinations?.Keys ?? Enumerable.Empty<string>()).Select(d => $"{c.ClusterId}/{d}"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _route.Keys.Where(k => !_routes.Contains(k)))
        {
            _route.TryRemove(key, out _);
        }

        foreach (var key in _cluster.Keys.Where(k => !_clusters.Contains(k)))
        {
            _cluster.TryRemove(key, out _);
        }

        foreach (var key in _destination.Keys.Where(k => !_destinations.Contains(k)))
        {
            _destination.TryRemove(key, out _);
        }

        _version = snapshot.Version;
    }
    private static Counters? Admit(ConcurrentDictionary<string, Counters> counters, HashSet<string>? active, string key)
    {
        if (active is not null && !active.Contains(key))
        {
            return null;
        }

        if (counters.TryGetValue(key, out var current))
        {
            return current;
        }

        return counters.Count < MaximumSeriesPerScope ? counters.GetOrAdd(key, _ => new()) : null;
    }

    private readonly ConcurrentDictionary<string, Counters> _route = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Counters> _cluster = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Counters> _destination = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string? routeId, string? clusterId, string? destinationId, int statusCode, long durationMicroseconds, long requestBytes, long responseBytes)
        => Record(routeId, clusterId, destinationId, statusCode, durationMicroseconds, requestBytes, responseBytes, PluginMetricOutcome.Completed);

    public void Record(string? routeId, string? clusterId, string? destinationId, int statusCode, long durationMicroseconds, long requestBytes, long responseBytes, PluginMetricOutcome outcome)
    {
        lock (_gate)
        {
            Refresh();
            if (routeId != null && Admit(_route, _routes, routeId) is { } route)
            {
                Record(route, statusCode, durationMicroseconds, requestBytes, responseBytes, outcome);
            }

            if (clusterId != null && Admit(_cluster, _clusters, clusterId) is { } cluster)
            {
                Record(cluster, statusCode, durationMicroseconds, requestBytes, responseBytes, outcome);
            }

            if (clusterId != null && destinationId != null && Admit(_destination, _destinations, $"{clusterId}/{destinationId}") is { } destination)
            {
                Record(destination, statusCode, durationMicroseconds, requestBytes, responseBytes, outcome);
            }
        }
    }

    public IReadOnlyDictionary<string, PluginMetricSnapshot> GetRoutes() { lock (_gate) { Refresh(); return Snapshot(_route); } }
    public IReadOnlyDictionary<string, PluginMetricSnapshot> GetClusters() { lock (_gate) { Refresh(); return Snapshot(_cluster); } }
    public IReadOnlyDictionary<string, PluginMetricSnapshot> GetDestinations() { lock (_gate) { Refresh(); return Snapshot(_destination); } }

    private static void Record(Counters counters, int statusCode, long duration, long requestBytes, long responseBytes, PluginMetricOutcome outcome)
    {
        Interlocked.Increment(ref counters.Requests);
        if (outcome == PluginMetricOutcome.Cancelled)
        {
            Interlocked.Increment(ref counters.Cancellations);
        }
        else if (outcome == PluginMetricOutcome.Faulted || statusCode >= 500)
        {
            Interlocked.Increment(ref counters.Errors);
        }

        if (outcome == PluginMetricOutcome.Faulted)
        {
            Interlocked.Increment(ref counters.Exceptions);
        }

        Interlocked.Add(ref counters.DurationMicroseconds, Math.Max(0, duration));
        Interlocked.Add(ref counters.RequestBytes, Math.Max(0, requestBytes));
        Interlocked.Add(ref counters.ResponseBytes, Math.Max(0, responseBytes));
    }

    private static Dictionary<string, PluginMetricSnapshot> Snapshot(ConcurrentDictionary<string, Counters> source) =>
        source.ToDictionary(pair => pair.Key, pair => new PluginMetricSnapshot(
            Interlocked.Read(ref pair.Value.Requests), Interlocked.Read(ref pair.Value.Errors),
            Interlocked.Read(ref pair.Value.RequestBytes), Interlocked.Read(ref pair.Value.ResponseBytes),
            Interlocked.Read(ref pair.Value.DurationMicroseconds))
            { Exceptions = Interlocked.Read(ref pair.Value.Exceptions), Cancellations = Interlocked.Read(ref pair.Value.Cancellations) }, StringComparer.OrdinalIgnoreCase);

    private sealed class Counters { public long Requests; public long Errors; public long RequestBytes; public long ResponseBytes; public long DurationMicroseconds; public long Exceptions; public long Cancellations; }
}

public sealed class PluginMetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly GatewayPluginExecutionPlanProvider _plans;
    private readonly LockFreeStatistics _statistics;
    private readonly PluginMetricStore _metricStore;

    public PluginMetricsMiddleware(RequestDelegate next, GatewayPluginExecutionPlanProvider plans, LockFreeStatistics statistics, PluginMetricStore metricStore)
    {
        _next = next;
        _plans = plans;
        _statistics = statistics;
        _metricStore = metricStore;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var feature = context.Features.Get<IReverseProxyFeature>();
        var routeId = feature?.Route?.Config?.RouteId;
        var clusterId = feature?.Cluster?.Config?.ClusterId;
        MetricsExecutionConfig? routeConfig = null;
        MetricsExecutionConfig? clusterConfig = null;
        var routeEnabled = routeId != null && _plans.Current.TrafficMetricsByRoute.TryGetValue(routeId, out routeConfig) && routeConfig.Enabled;
        var clusterEnabled = clusterId != null && _plans.Current.ClusterMetricsByCluster.TryGetValue(clusterId, out clusterConfig) && clusterConfig.Enabled;
        if (!routeEnabled && !clusterEnabled)
        {
            await _next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var outcome = PluginMetricOutcome.Completed;
        try { await _next(context); }
        catch (OperationCanceledException) { outcome = PluginMetricOutcome.Cancelled; throw; }
        catch { outcome = PluginMetricOutcome.Faulted; throw; }
        finally
        {
            if (outcome == PluginMetricOutcome.Completed && context.RequestAborted.IsCancellationRequested)
            {
                outcome = PluginMetricOutcome.Cancelled;
            }

            var measuredStatus = outcome switch
            {
                PluginMetricOutcome.Cancelled => 499,
                PluginMetricOutcome.Faulted => 500,
                _ => context.Response.StatusCode
            };
            var elapsedMicros = (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds * 1000d);
            var destinationId = feature?.ProxiedDestination?.DestinationId;
            var requestBytes = routeEnabled && routeConfig!.IncludeRequestBytes ? context.Request.ContentLength ?? 0 : 0;
            var responseBytes = routeEnabled && routeConfig!.IncludeResponseBytes ? context.Response.ContentLength ?? 0 : 0;
            _metricStore.Record(routeEnabled ? routeId : null, clusterEnabled ? clusterId : null,
                clusterEnabled && clusterConfig!.IncludeDestination ? destinationId : null,
                measuredStatus, elapsedMicros, requestBytes, responseBytes, outcome);
            _statistics.RecordRequest(
                measuredStatus,
                elapsedMicros,
                routeId == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(routeId),
                clusterId == null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(clusterId));
        }
    }
}
