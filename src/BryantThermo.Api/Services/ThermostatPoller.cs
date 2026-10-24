using BryantThermo.Api.Carrier;
using BryantThermo.Api.Models;
using Microsoft.Extensions.Options;

namespace BryantThermo.Api.Services;

/// <summary>Latest known thermostat state plus polling health. Thread-safe; read by the HTTP endpoints.</summary>
public sealed class ThermostatState
{
    private volatile ThermostatSnapshot? _snapshot;
    private volatile string? _lastError;
    private long _lastErrorTicks;

    public ThermostatSnapshot? Snapshot => _snapshot;
    public string? LastError => _lastError;
    public DateTimeOffset? LastErrorAtUtc =>
        Interlocked.Read(ref _lastErrorTicks) is var t and > 0 ? new DateTimeOffset(t, TimeSpan.Zero) : null;

    public void SetSnapshot(ThermostatSnapshot snapshot)
    {
        _snapshot = snapshot;
        _lastError = null;
    }

    public void SetError(string message)
    {
        _lastError = message;
        Interlocked.Exchange(ref _lastErrorTicks, DateTimeOffset.UtcNow.UtcTicks);
    }
}

/// <summary>
/// Polls Carrier's cloud on a fixed interval so front-end requests are served from memory and never
/// hammer Carrier (which can rate-limit or lock accounts). Also supports an on-demand refresh.
/// </summary>
public sealed class ThermostatPoller(
    CarrierCloudClient client,
    ThermostatState state,
    IOptions<CarrierOptions> options,
    TimeProvider time,
    ILogger<ThermostatPoller> log) : BackgroundService
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MinManualGap = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _wake = new(0, 1);
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;

    /// <summary>Asks the poller to refresh now. Returns false if a poll happened too recently.</summary>
    public bool RequestRefresh()
    {
        if (time.GetUtcNow() - _lastPoll < MinManualGap) return false;
        try { _wake.Release(); } catch (SemaphoreFullException) { /* already pending */ }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        if (interval < MinInterval) interval = MinInterval;
        var backoff = interval;

        while (!stoppingToken.IsCancellationRequested)
        {
            _lastPoll = time.GetUtcNow();
            try
            {
                var systems = await client.GetSystemsAsync(stoppingToken);
                var snapshot = CarrierStatusMapper.MapSystem(systems, options.Value.SystemSerial, time.GetUtcNow());
                state.SetSnapshot(snapshot);
                backoff = interval;
                log.LogDebug("Polled {Serial}: {Zones} zone(s).", snapshot.SystemSerial, snapshot.Zones.Count);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                state.SetError(ex.Message);
                log.LogWarning(ex, "Polling Carrier failed.");
                // Back off on repeated failures (especially auth) so we don't trip account lockouts.
                backoff = ex is CarrierAuthException
                    ? TimeSpan.FromMinutes(15)
                    : TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 900));
            }

            try
            {
                await _wake.WaitAsync(backoff, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
