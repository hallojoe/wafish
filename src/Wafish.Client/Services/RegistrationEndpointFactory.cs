using Grpc.Net.Client;
using GatewayGrpc = Wafish.GatewayRegistry.GatewayRegistry;

namespace Wafish.Services;

/// <summary>Creates registration clients for explicitly trusted gRPC endpoints. Implementations own their transports.</summary>
public interface IRegistrationEndpointFactory
{
    GatewayGrpc.GatewayRegistryClient Create(string endpoint);
}

internal sealed class RegistrationEndpointFactory : IRegistrationEndpointFactory, IDisposable
{
    private readonly Dictionary<string, GrpcChannel> _channels = new(StringComparer.Ordinal);
    public GatewayGrpc.GatewayRegistryClient Create(string endpoint)
    {
        lock (_channels)
        {
            if (!_channels.TryGetValue(endpoint, out var channel))
            {
                channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
                { HttpHandler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true } });
                _channels.Add(endpoint, channel);
            }
            return new(channel);
        }
    }
    public void Dispose()
    {
        lock (_channels)
        {
            foreach (var channel in _channels.Values) channel.Dispose();
            _channels.Clear();
        }
    }
}
