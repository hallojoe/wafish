using Wafish.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wafish.Services;

/// <summary>
/// Auto-registration hosted service: registers on start (with exponential backoff retry),
/// sends periodic heartbeat, unregisters on stop.
/// </summary>
internal sealed class GatewayRegistrationHostedService : IHostedService, IAsyncDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly Random _random;
    private readonly GatewayAutoRegistrationClient _client;
    private readonly Grpc.Net.Client.GrpcChannel _grpcChannel;
    private readonly ILogger<GatewayRegistrationHostedService> _logger;
    private readonly GatewayRegistrationOptions _options;
    private readonly CancellationTokenSource _heartbeatLifetime = new();
    private Task? _heartbeatTask;

    private const int MaxRetries = 5;
    private const int MinHeartbeatIntervalSeconds = 5;
    private const int MaxHeartbeatIntervalSeconds = 3600;
    private const int DefaultHeartbeatIntervalSeconds = 30;

    /// <summary>
    /// Consecutive heartbeat failures tolerated before attempting to re-register.
    /// Covers gateway restarts / lost registrations (e.g. gateway state reset).
    /// </summary>
    private const int MaxConsecutiveHeartbeatFailures = 3;
    private int _consecutiveHeartbeatFailures;

    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30)
    };

    public GatewayRegistrationHostedService(
        GatewayAutoRegistrationClient client,
        Grpc.Net.Client.GrpcChannel grpcChannel,
        ILogger<GatewayRegistrationHostedService> logger,
        IOptions<GatewayRegistrationOptions> options, TimeProvider? timeProvider = null, Random? random = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _random = random ?? Random.Shared;
        _client = client;
        _grpcChannel = grpcChannel;
        _logger = logger;
        _options = options.Value;
    }

    internal static TimeSpan RetryDelay(int attempt, Random random)
    {
        var maximum = RetryDelays[Math.Clamp(attempt, 0, RetryDelays.Length - 1)];
        return TimeSpan.FromMilliseconds(maximum.TotalMilliseconds * (0.5 + random.NextDouble() * 0.5));
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (!RegistrationOptionsResolver.IsEnabled(_options))
        {
            _logger.LogDebug("Auto-registration disabled (no GatewayUrl configured), skipping");
            return;
        }

        if (_options.UseGrpcRegistration == true)
        {
            if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))
            {
                _logger.LogInformation("gRPC registration mode: dialing gateway gRPC endpoint {GrpcTarget}",
                    _grpcChannel.Target);
            }
        }

        _logger.LogDebug("Auto-registration starting...");

        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                if (await _client.RegisterAsync(ct).ConfigureAwait(false))
                {
                    _logger.LogDebug("Auto-registration complete");
                    StartHeartbeatIfEnabled(ct);
                    return;
                }

                // Non-exception failure (e.g. gateway returned error), still retry
                if (attempt < MaxRetries - 1)
                {
                    var delay = RetryDelay(attempt, _random);
                    if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))
                    {
                        _logger.LogInformation(
                            "Registration attempt {Attempt}/{MaxRetries} failed, retrying in {Delay}s...",
                            attempt + 1, MaxRetries, delay.TotalSeconds);
                    }
                    await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Auto-registration cancelled during shutdown");
                return;
            }
            catch (Exception ex)
            {
                if (attempt < MaxRetries - 1)
                {
                    var delay = RetryDelay(attempt, _random);
                    _logger.LogWarning(ex,
                        "Registration attempt {Attempt}/{MaxRetries} failed, retrying in {Delay}s...",
                        attempt + 1, MaxRetries, delay.TotalSeconds);
                    try
                    {
                        await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                else
                {
                    _logger.LogWarning(ex,
                        "Auto-registration failed after {MaxRetries} attempts, service continues without gateway registration",
                        MaxRetries);
                }
            }
        }
        StartHeartbeatIfEnabled(ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        // Stop heartbeat
        await _heartbeatLifetime.CancelAsync();
        if (_heartbeatTask != null)
        {
            await _heartbeatTask.ConfigureAwait(false);
        }

        _logger.LogInformation("Auto-unregistration starting...");
        try
        {
            if (_options.DrainSeconds > 0 && await _client.DrainAsync(ct).ConfigureAwait(false))
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(300, _options.DrainSeconds)), _timeProvider, ct).ConfigureAwait(false);
            if (await _client.UnregisterAsync(ct).ConfigureAwait(false))
            {
                _logger.LogInformation("Auto-unregistration complete");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-unregistration error, service shuts down normally");
        }
    }

    /// <summary>
    /// Start periodic heartbeat to keep the registration alive.
    /// Respects <see cref="GatewayRegistrationOptions.HeartbeatEnabled"/> and <see cref="GatewayRegistrationOptions.HeartbeatIntervalSeconds"/>.
    /// Gateway can detect stale registrations if heartbeat stops.
    /// </summary>
    private void StartHeartbeatIfEnabled(CancellationToken ct)
    {
        var heartbeatEnabled = _options.HeartbeatEnabled ?? true;

        if (!heartbeatEnabled)
        {
            _logger.LogInformation("Heartbeat disabled by configuration");
            return;
        }

        var intervalSeconds = _options.HeartbeatIntervalSeconds ?? DefaultHeartbeatIntervalSeconds;

        // Clamp to valid range
        if (intervalSeconds < MinHeartbeatIntervalSeconds)
        {
            _logger.LogWarning(
                "Heartbeat interval {Configured}s is below minimum {Min}s, using {Effective}s",
                intervalSeconds, MinHeartbeatIntervalSeconds, MinHeartbeatIntervalSeconds);
            intervalSeconds = MinHeartbeatIntervalSeconds;
        }
        else if (intervalSeconds > MaxHeartbeatIntervalSeconds)
        {
            _logger.LogWarning(
                "Heartbeat interval {Configured}s exceeds maximum {Max}s, using {Effective}s",
                intervalSeconds, MaxHeartbeatIntervalSeconds, MaxHeartbeatIntervalSeconds);
            intervalSeconds = MaxHeartbeatIntervalSeconds;
        }

        if (_options.LeaseDurationSeconds > 0) intervalSeconds = Math.Min(intervalSeconds, Math.Max(5, _options.LeaseDurationSeconds / 3));
        if (_client.LeaseRenewalSeconds is { } renewal) intervalSeconds = Math.Min(intervalSeconds, renewal);
        var interval = TimeSpan.FromSeconds(intervalSeconds);

        _heartbeatTask = RunHeartbeatsAsync(interval, _heartbeatLifetime.Token);

        if (_logger.IsEnabled(global::Microsoft.Extensions.Logging.LogLevel.Information))
        {
            _logger.LogInformation("Heartbeat started (interval: {Interval}s)", interval.TotalSeconds);
        }
    }
    public async ValueTask DisposeAsync()
    {
        await _heartbeatLifetime.CancelAsync();
        if (_heartbeatTask != null)
        {
            await _heartbeatTask.ConfigureAwait(false);
        }

        _heartbeatLifetime.Dispose();
    }

    private async Task RunHeartbeatsAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var healthy = await _client.HeartbeatAsync(ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (healthy)
                    {
                        _consecutiveHeartbeatFailures = 0;
                    }
                    else if (++_consecutiveHeartbeatFailures >= MaxConsecutiveHeartbeatFailures)
                    {
                        if (await _client.RegisterAsync(ct).ConfigureAwait(false))
                        {
                            _consecutiveHeartbeatFailures = 0;
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Heartbeat failed"); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

}
