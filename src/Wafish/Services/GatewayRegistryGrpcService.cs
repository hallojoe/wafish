using GatewayGrpc = Wafish.GatewayRegistry.GatewayRegistry;
using Wafish.GatewayRegistry;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace Wafish.Services;

public class GatewayRegistryGrpcService : GatewayGrpc.GatewayRegistryBase
{
    private readonly DynamicYarpConfigService _dynamicConfigService;
    private readonly ILogger<GatewayRegistryGrpcService> _logger;

    public GatewayRegistryGrpcService(
        DynamicYarpConfigService dynamicConfigService,
        ILogger<GatewayRegistryGrpcService> logger)
    {
        _dynamicConfigService = dynamicConfigService;
        _logger = logger;
    }

    public override async Task<RegisterServiceResponse> RegisterService(RegisterServiceRequest request, ServerCallContext context)
    {
        try
        {
            return await RegisterServiceInternalAsync(request, context);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC RegisterService unhandled exception for ServiceId={ServiceId}", request?.ServiceId);
            throw new RpcException(new Status(StatusCode.Internal, "Registration failed. Inspect the gateway before retrying."));
        }
    }

    private async Task<RegisterServiceResponse> RegisterServiceInternalAsync(RegisterServiceRequest request, ServerCallContext context)
    {
        if (request.Destinations.Count == 0 || request.Destinations.All(d => string.IsNullOrWhiteSpace(d.Address)))
        {
            return new RegisterServiceResponse
            {
                Success = false, Status = "ValidationRejected",
                Message = "At least one valid destination address is required"
            };
        }

        GrpcGatewayRegistryMapper.LogUnsupportedPathsIfNeeded(request, _logger);

        // Phase 2: multi-path → multiple routes sharing the same cluster
        var routeRequests = GrpcGatewayRegistryMapper.ToRegisterRouteRequests(request);
        var clusterName = GrpcGatewayRegistryMapper.BuildClusterName(request);
        var destinations = GrpcGatewayRegistryMapper.BuildDestinations(request);
        var loadBalancingPolicy = GrpcGatewayRegistryMapper.MapLoadBalancingPolicy(request.LoadBalancing);

        var cluster = new global::Yarp.ReverseProxy.Configuration.ClusterConfig
        {
            ClusterId = clusterName, LoadBalancingPolicy = loadBalancingPolicy,
            Destinations = destinations.ToDictionary(d => d.Key,
                d => new global::Yarp.ReverseProxy.Configuration.DestinationConfig { Address = d.Value }),
            Metadata = request.Metadata.ToDictionary(p => p.Key, p => p.Value)
        };
        var routes = routeRequests.Select(r => new global::Yarp.ReverseProxy.Configuration.RouteConfig
        {
            RouteId = r.RouteName, ClusterId = clusterName, Order = r.Order,
            Match = new() { Path = r.MatchPath }
        }).ToList();
        var result = string.IsNullOrWhiteSpace(request.InstanceId)
            ? await _dynamicConfigService.ImportBatchAsync([cluster], routes, "grpc", "grpc-client", null, context.CancellationToken)
            : await _dynamicConfigService.RegisterInstanceAsync(GrpcGatewayRegistryMapper.BuildRouteName(request), request.InstanceId, cluster, routes, context.CancellationToken);
        var response = new RegisterServiceResponse
        {
            Success = result.Success, Message = result.Message, Status = result.Status.ToString(),
            ClusterId = clusterName, RegisteredAt = result.Success ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : 0
        };
        if (result.PersistedVersion is { } stored)
        {
            response.PersistedVersion = stored;
        }

        if (result.ActiveVersion is { } active)
        {
            response.ActiveVersion = active;
        }

        if (result.Success)
        {
            response.RouteIds.AddRange(routes.Select(r => r.RouteId));
        }

        return response;
    }

    public override async Task<UnregisterServiceResponse> UnregisterService(UnregisterServiceRequest request, ServerCallContext context)
    {
        try
        {
            var serviceId = string.IsNullOrWhiteSpace(request.ServiceId) ? "grpc-service" : request.ServiceId;

            if (!string.IsNullOrWhiteSpace(request.InstanceId))
            {
                var removal = await _dynamicConfigService.UnregisterInstanceAsync(serviceId, request.InstanceId, context.CancellationToken);
                var instanceResponse = new UnregisterServiceResponse
                { Success = removal.Success, Message = removal.Message, Status = removal.Status.ToString() };
                if (removal.PersistedVersion is { } instanceStored)
                {
                    instanceResponse.PersistedVersion = instanceStored;
                }

                if (removal.ActiveVersion is { } instanceActive)
                {
                    instanceResponse.ActiveVersion = instanceActive;
                }

                return instanceResponse;
            }

            // Multi-path registration creates routes named "{serviceId}" (single path)
            // or "{serviceId}-path{N}" (one per path). Remove them all; TryRemoveRoute
            // already cleans up the cluster when the last referencing route is removed.
            var routeNames = FindServiceRouteNames(serviceId);
            if (routeNames.Count == 0)
            {
                return new UnregisterServiceResponse
                {
                    Success = false, Status = "NotFound",
                    Message = $"Route '{serviceId}' not found"
                };
            }

            var result = await _dynamicConfigService.BatchDeleteRoutesAsync(routeNames, removeOrphanedClusters: true, "grpc-client", context.CancellationToken);
            var response = new UnregisterServiceResponse
            { Success = result.Success, Message = result.Message, Status = result.Status.ToString() };
            if (result.PersistedVersion is { } stored)
            {
                response.PersistedVersion = stored;
            }

            if (result.ActiveVersion is { } active)
            {
                response.ActiveVersion = active;
            }

            return response;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC UnregisterService exception for ServiceId={ServiceId}", request?.ServiceId);
            throw new RpcException(new Status(StatusCode.Internal, "Unregistration failed. Inspect the gateway before retrying."));
        }
    }

    public override Task<HeartbeatResponse> Heartbeat(HeartbeatRequest request, ServerCallContext context)
    {
        try
        {
            var serviceId = string.IsNullOrWhiteSpace(request.ServiceId) ? "grpc-service" : request.ServiceId;

            // Resolve the route name: "{serviceId}" when present, otherwise any
            // "{serviceId}-path{N}" route created by multi-path registration.
            var routeName = ResolveHeartbeatRouteName(serviceId);
            var updated = routeName != null && (string.IsNullOrWhiteSpace(request.InstanceId) ||
                _dynamicConfigService.IsInstanceRegistered(serviceId, request.InstanceId)) && _dynamicConfigService.UpdateHeartbeat(routeName);

            return Task.FromResult(new HeartbeatResponse
            {
                Success = updated,
                Message = updated ? "heartbeat" : $"Route '{serviceId}' not found",
                NextHeartbeat = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds()
            });
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC Heartbeat exception for ServiceId={ServiceId}", request?.ServiceId);
            throw new RpcException(new Status(StatusCode.Internal, "Heartbeat failed."));
        }
    }

    /// <summary>
    /// Find all route names belonging to a service: the exact "{serviceId}" route and any
    /// "{serviceId}-path{N}" routes created by multi-path registration.
    /// </summary>
    private List<string> FindServiceRouteNames(string serviceId)
    {
        var names = new List<string>();
        var config = _dynamicConfigService.GetDynamicConfig();
        if (config == null)
        {
            return names;
        }

        foreach (var route in config.Routes)
        {
            var routeId = route.Config.RouteId;
            if (string.IsNullOrWhiteSpace(routeId))
            {
                continue;
            }

            if (routeId.Equals(serviceId, StringComparison.OrdinalIgnoreCase) ||
                IsMultiPathRouteName(routeId, serviceId))
            {
                names.Add(routeId);
            }
        }

        return names;
    }

    /// <summary>
    /// Resolve a route name usable for heartbeat: the exact "{serviceId}" route, or the first
    /// "{serviceId}-path{N}" route when multi-path registration was used. Returns null when none exist.
    /// </summary>
    private string? ResolveHeartbeatRouteName(string serviceId)
    {
        var config = _dynamicConfigService.GetDynamicConfig();
        if (config == null)
        {
            return null;
        }

        foreach (var route in config.Routes)
        {
            var routeId = route.Config.RouteId;
            if (string.IsNullOrWhiteSpace(routeId))
            {
                continue;
            }

            if (routeId.Equals(serviceId, StringComparison.OrdinalIgnoreCase))
            {
                return routeId;
            }
        }

        foreach (var route in config.Routes)
        {
            var routeId = route.Config.RouteId;
            if (string.IsNullOrWhiteSpace(routeId))
            {
                continue;
            }

            if (IsMultiPathRouteName(routeId, serviceId))
            {
                return routeId;
            }
        }

        return null;
    }

    /// <summary>Checks whether "{routeId}" is a multi-path child route of "{serviceId}" (i.e. "{serviceId}-path{N}").</summary>
    private static bool IsMultiPathRouteName(string routeId, string serviceId)
    {
        return routeId.Length > serviceId.Length + 5 &&
            routeId.StartsWith(serviceId + "-path", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(routeId[(serviceId.Length + 5)..], out _);
    }

    public override Task<GetServicesResponse> GetServices(GetServicesRequest request, ServerCallContext context)
    {
        try
        {
            var dynamicConfig = _dynamicConfigService.GetDynamicConfig();
            var services = new List<ServiceInfo>();

            if (dynamicConfig != null)
            {
                foreach (var route in dynamicConfig.Routes.Where(r => string.Equals(r.Source, "grpc", StringComparison.OrdinalIgnoreCase)))
                {
                    var cluster = dynamicConfig.Clusters.FirstOrDefault(c => string.Equals(c.Config.ClusterId, route.Config.ClusterId, StringComparison.OrdinalIgnoreCase));
                    if (request.ActiveOnly && cluster == null)
                    {
                        continue;
                    }

                    var serviceInfo = new ServiceInfo
                    {
                        ServiceId = route.Config.RouteId,
                        ServiceName = route.Config.ClusterId,
                        ClusterId = route.Config.ClusterId,
                        IsHealthy = true,
                        RegisteredAt = new DateTimeOffset(route.CreatedAt).ToUnixTimeSeconds(),
                        LastHeartbeat = cluster?.LastHeartbeat != null
                            ? new DateTimeOffset(cluster.LastHeartbeat.Value).ToUnixTimeSeconds()
                            : 0
                    };

                    var matchPath = route.Config.Match?.Path;
                    if (!string.IsNullOrWhiteSpace(matchPath))
                    {
                        serviceInfo.Paths.Add(matchPath);
                    }

                    if (cluster?.Config.Destinations != null)
                    {
                        foreach (var destination in cluster.Config.Destinations)
                        {
                            serviceInfo.Destinations.Add(new DestinationInfo
                            {
                                DestinationId = destination.Key,
                                Address = destination.Value.Address ?? string.Empty,
                                IsHealthy = true,
                                IsEnabled = true,
                                HealthRatio = 1
                            });
                        }
                    }

                    if (cluster?.Config.Metadata != null)
                    {
                        foreach (var kv in cluster.Config.Metadata)
                        {
                            serviceInfo.Metadata[kv.Key] = kv.Value;
                        }
                    }

                    services.Add(serviceInfo);
                }
            }

            var response = new GetServicesResponse();
            response.Services.AddRange(services);
            return Task.FromResult(response);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC GetServices exception");
            throw new RpcException(new Status(StatusCode.Internal, "Service lookup failed."));
        }
    }

    public override async Task<UpdateDestinationsResponse> UpdateDestinations(UpdateDestinationsRequest request, ServerCallContext context)
    {
        try
        {
            if (request.Destinations.Count == 0)
            {
                return new UpdateDestinationsResponse
                {
                    Success = false, Status = "ValidationRejected",
                    Message = "At least one destination is required"
                };
            }

            if (string.IsNullOrWhiteSpace(request.InstanceId) || !request.HasExpectedVersion)
            {
                return new UpdateDestinationsResponse { Success = false, Status = "ValidationRejected",
                    Message = "InstanceId and ExpectedVersion are required. Use administration for full-cluster replacement." };
            }

            if (request.Destinations.Any(d => string.IsNullOrWhiteSpace(d.DestinationId) || string.IsNullOrWhiteSpace(d.Address)) ||
                request.Destinations.Select(d => d.DestinationId).Distinct(StringComparer.Ordinal).Count() != request.Destinations.Count)
                return new UpdateDestinationsResponse { Success = false, Status = "ValidationRejected", Message = "Destination IDs must be unique and addresses must be present." };
            var destinations = request.Destinations.ToDictionary(d => d.DestinationId, d => d.Address);
            var result = await _dynamicConfigService.UpdateInstanceDestinationsAsync(
                request.ServiceId, request.InstanceId, destinations, request.ExpectedVersion, context.CancellationToken);

            var response = new UpdateDestinationsResponse
            { Success = result.Success, Message = result.Message, Status = result.Status.ToString() };
            if (result.PersistedVersion is { } stored)
            {
                response.PersistedVersion = stored;
            }

            if (result.ActiveVersion is { } active)
            {
                response.ActiveVersion = active;
            }

            return response;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "gRPC UpdateDestinations exception for ServiceId={ServiceId}", request?.ServiceId);
            throw new RpcException(new Status(StatusCode.Internal, "Destination update failed. Inspect the gateway before retrying."));
        }
    }
}
