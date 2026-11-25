using Yarp.ReverseProxy.Forwarder;

namespace Wafish.Performance;

internal sealed class BenchmarkForwarderHttpClientFactory(BenchmarkTransport transport) : ForwarderHttpClientFactory
{
    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        base.ConfigureHandler(context, handler);
        transport.ConfigureHandler(handler);
    }
}
