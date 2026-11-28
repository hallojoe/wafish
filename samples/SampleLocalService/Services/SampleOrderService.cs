using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SampleLocalService.Services;

public interface ISampleOrderService
{
    SampleOrderResult Create(string key, CreateSampleOrderRequest request, CancellationToken cancellationToken);
    SampleOrderResponse? Get(Guid id, CancellationToken cancellationToken);
}

/// <summary>Atomically retains bounded synthetic orders within one backend process.</summary>
public sealed class SampleOrderService(TimeProvider clock, SampleInstanceIdentity identity) : ISampleOrderService
{
    public const int Capacity = 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private sealed record Entry(CreateSampleOrderRequest Request, SampleOrderResponse Order, long Created);

    public static bool IsValidKey(string? key) => key is { Length: >= 16 and <= 64 } &&
        key.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public SampleOrderResult Create(string key, CreateSampleOrderRequest request, CancellationToken cancellationToken)
    {
        if (!IsValidKey(key) || request.Product is not ("sample-widget" or "sample-gadget") || request.Quantity is < 1 or > 100)
            throw new ArgumentException("Use a bounded synthetic order and idempotency key.");
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = clock.GetTimestamp();
            Prune(now);
            if (_entries.TryGetValue(key, out var existing))
                return existing.Request == request ? new(SampleOrderOutcome.Replayed, existing.Order) : new(SampleOrderOutcome.Conflict, null);
            if (_entries.Count == Capacity)
            {
                var remaining = _entries.Values.Min(entry => (Retention - clock.GetElapsedTime(entry.Created, now)).TotalSeconds);
                return new(SampleOrderOutcome.Capacity, null, Math.Max(1, (int)Math.Ceiling(remaining)));
            }
            var created = clock.GetUtcNow();
            var order = new SampleOrderResponse(Guid.NewGuid(), request.Product, request.Quantity, identity.Value, created, created + Retention);
            _entries.Add(key, new(request, order, now));
            return new(SampleOrderOutcome.Created, order);
        }
    }

    public SampleOrderResponse? Get(Guid id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Prune(clock.GetTimestamp());
            return _entries.Values.FirstOrDefault(entry => entry.Order.Id == id)?.Order;
        }
    }

    private void Prune(long now)
    {
        foreach (var key in _entries.Where(pair => clock.GetElapsedTime(pair.Value.Created, now) >= Retention).Select(pair => pair.Key).ToArray())
            _entries.Remove(key);
    }
}

public enum SampleOrderOutcome { Created, Replayed, Conflict, Capacity }
public sealed record SampleOrderResult(SampleOrderOutcome Outcome, SampleOrderResponse? Order, int RetryAfterSeconds = 0);

/// <summary>A synthetic product and quantity. No customer, payment, or delivery data is accepted.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateSampleOrderRequest
{
    [Required, AllowedValues("sample-widget", "sample-gadget")] public string? Product { get; init; }
    [Range(1, 100)] public int Quantity { get; init; }
}

/// <summary>A process-local synthetic order retained for five minutes.</summary>
public sealed record SampleOrderResponse(Guid Id, string Product, int Quantity, string Instance, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public static class SampleOrderExtensions
{
    public static IServiceCollection AddSampleOrders(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISampleOrderService, SampleOrderService>();
        services.AddProblemDetails();
        services.AddExceptionHandler<SampleLocalService.Middleware.SampleRequestExceptionHandler>();
        services.AddOpenApi();
        return services;
    }
}
