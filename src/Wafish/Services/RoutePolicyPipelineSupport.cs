namespace Wafish.Services;

/// <summary>Host-local support for policies that must run before endpoint responses.</summary>
public sealed class RoutePolicyPipelineSupport
{
    private int _disabled;
    public bool Enabled => Volatile.Read(ref _disabled) == 0;
    public void Disable() => Interlocked.Exchange(ref _disabled, 1);
    public void RequireEnabled()
    {
        if (!Enabled) throw new ArgumentException("Route endpoint policies require the built-in Wafish proxy pipeline.");
    }
}
