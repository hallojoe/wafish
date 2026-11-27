namespace Wafish.Services;

/// <summary>Immutable configuration shared by one internal execution flow.</summary>
public static class RequestExecutionSnapshot
{
    private static readonly AsyncLocal<GatewaySnapshot?> Ambient = new();
    public static GatewaySnapshot? Current => Ambient.Value;
    public static IDisposable Enter(GatewaySnapshot snapshot)
    {
        var previous = Ambient.Value;
        Ambient.Value = snapshot;
        return new Scope(previous);
    }
    private sealed class Scope(GatewaySnapshot? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; Ambient.Value = previous; }
    }
}
