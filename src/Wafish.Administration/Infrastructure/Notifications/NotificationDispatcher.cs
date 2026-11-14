using System.Threading.Channels;
using Wafish.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Wafish.Dashboard.Infrastructure.Notifications;

/// <summary>
/// Central hub for all outbound webhook notifications. Events (config changes and
/// gateway alerts) are enqueued from anywhere in the app and processed on a
/// background channel loop with per-(platform, event, target) cooldown dedup
/// so alert storms (e.g. a flapping circuit breaker) cannot flood webhook rate limits.
/// </summary>
public sealed class NotificationDispatcher : BackgroundService
{
    private readonly Channel<WebhookNotificationMessage> _queue;
    private long _submitted, _delivered, _failed, _discarded, _suppressed, _inFlight;

    public NotificationDispatchSnapshot GetSnapshot() => new(
        Interlocked.Read(ref _submitted), _queue.Reader.Count, Interlocked.Read(ref _inFlight),
        Interlocked.Read(ref _delivered), Interlocked.Read(ref _failed),
        Interlocked.Read(ref _discarded), Interlocked.Read(ref _suppressed));
    private readonly IServiceProvider _services;
    private readonly ILogger<NotificationDispatcher> _logger;

    private readonly object _cooldownLock = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldownUntil = new();

    public NotificationDispatcher(
        IServiceProvider services,
        ILogger<NotificationDispatcher> logger)
    {
        _services = services;
        _logger = logger;
        _queue = Channel.CreateBounded<WebhookNotificationMessage>(
            new BoundedChannelOptions(1000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            }, _ => Interlocked.Increment(ref _discarded));
    }

    /// <summary>Enqueue a config-change notification (fire-and-forget).</summary>
    public void EnqueueConfigChange(string eventType, string? target, string? operatorName)
        => Enqueue(new WebhookNotificationMessage
        {
            EventType = eventType,
            Target = target,
            Operator = operatorName,
            Severity = "Info"
        });

    /// <summary>Enqueue a gateway alert notification (fire-and-forget).</summary>
    public void EnqueueAlert(string alertType, string? target, string message, string severity = "Warning")
        => Enqueue(new WebhookNotificationMessage
        {
            EventType = alertType,
            Target = target,
            Detail = message,
            Severity = severity
        });

    /// <summary>Reset all cooldowns so suppressed events deliver again immediately.</summary>
    public void ClearCooldown()
    {
        lock (_cooldownLock)
        {
            _cooldownUntil.Clear();
        }
    }

    private void Enqueue(WebhookNotificationMessage message)
    {
        Interlocked.Increment(ref _submitted);
        if (!_queue.Writer.TryWrite(message))
        {
            Interlocked.Increment(ref _discarded);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
        // BackgroundService can cancel its scheduled task before ExecuteAsync begins.
        if (ExecuteTask is null || ExecuteTask.IsCompleted)
        {
            DiscardQueued();
        }
    }

    private void DiscardQueued()
    {
        while (_queue.Reader.TryRead(out _))
        {
            Interlocked.Increment(ref _discarded);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                Interlocked.Increment(ref _inFlight);
                try
                {
                    var outcome = await DispatchAsync(message, stoppingToken);
                    switch (outcome)
                    {
                        case DispatchOutcome.Delivered: Interlocked.Increment(ref _delivered); break;
                        case DispatchOutcome.Failed: Interlocked.Increment(ref _failed); break;
                        case DispatchOutcome.Suppressed: Interlocked.Increment(ref _suppressed); break;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _discarded);
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    _logger.LogWarning(ex, "Failed to dispatch webhook notification {EventType}", message.EventType);
                }
                finally { Interlocked.Decrement(ref _inFlight); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _queue.Writer.TryComplete();
            DiscardQueued();
        }
    }

    private enum DispatchOutcome { Delivered, Failed, Suppressed }

    private async Task<DispatchOutcome> DispatchAsync(WebhookNotificationMessage message, CancellationToken ct)
    {
        WebhookSettingsData settings;
        WebhookNotificationService sender;
        try
        {
            using (var scope = _services.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IWebhookSettingsRepository>();
                settings = await repo.LoadAsync(ct);
            }
            sender = _services.GetRequiredService<WebhookNotificationService>();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load webhook settings for {EventType}", message.EventType);
            return DispatchOutcome.Failed;
        }

        if (settings.Platforms.Count == 0)
        {
            return DispatchOutcome.Suppressed;
        }

        // Route: platforms with an explicit subscription list only receive their
        // subscribed events; platforms without one fall back to the global list.
        var platforms = new List<string>();
        foreach (var platform in settings.Platforms.Keys)
        {
            var subscribed = settings.PlatformEvents.TryGetValue(platform, out var events)
                ? events.Contains(message.EventType, StringComparer.OrdinalIgnoreCase)
                : settings.EnabledEvents.Contains(message.EventType, StringComparer.OrdinalIgnoreCase);

            if (subscribed)
            {
                platforms.Add(platform);
            }
        }

        if (platforms.Count == 0)
        {
            return DispatchOutcome.Suppressed;
        }

        // Cooldown dedup per (platform, event, target).
        var cooldown = TimeSpan.FromSeconds(Math.Clamp(settings.CooldownSeconds, 0, 3600));
        if (cooldown > TimeSpan.Zero)
        {
            var now = DateTimeOffset.UtcNow;
            lock (_cooldownLock)
            {
                platforms.RemoveAll(p =>
                {
                    var key = $"{p}|{message.EventType}|{message.Target ?? "-"}";
                    if (_cooldownUntil.TryGetValue(key, out var until) && until > now)
                    {
                        return true;
                    }

                    _cooldownUntil[key] = now + cooldown;
                    return false;
                });

                if (_cooldownUntil.Count > 5000)
                {
                    _cooldownUntil.Clear(); // safety valve for unbounded key growth
                }
            }

            if (platforms.Count == 0)
            {
                if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Debug))
                {
                    _logger.LogDebug("Notification {EventType} suppressed by cooldown", message.EventType);
                }
                return DispatchOutcome.Suppressed;
            }
        }

        var report = await sender.SendToPlatformsAsync(message, platforms, ct: ct);
        if (!report.Success)
        {
            ct.ThrowIfCancellationRequested();
        }

        if (report.Total > 0 && !report.Success)
        {
            _logger.LogWarning(
                "Webhook notification {EventType} partially failed: {Succeeded}/{Total} delivered",
                message.EventType, report.Succeeded, report.Total);
        }
        return report.Total == 0 ? DispatchOutcome.Suppressed :
            report.Success ? DispatchOutcome.Delivered : DispatchOutcome.Failed;
    }
}

/// <summary>Event counts. Partial endpoint delivery counts as one failed event.</summary>
public sealed record NotificationDispatchSnapshot(long Submitted, long Queued, long InFlight,
    long Delivered, long Failed, long Discarded, long Suppressed);
