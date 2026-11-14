using System.Diagnostics;

namespace SampleLocalService;

public sealed record SampleInstanceIdentity(string Value)
{
    public static SampleInstanceIdentity Resolve(IConfiguration configuration)
    {
        var configured = configuration["Gateway:Registration:InstanceId"];
        return new(string.IsNullOrWhiteSpace(configured) ? "standalone-" + Guid.NewGuid().ToString("N") : configured);
    }

    public void Apply(HttpContext context)
    {
        context.Response.Headers["X-Backend-Instance"] = Value;
        Activity.Current?.SetTag("gateway.destination.instance", Value);
    }
}
