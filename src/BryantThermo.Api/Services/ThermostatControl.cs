using BryantThermo.Api.Carrier;
using BryantThermo.Api.Models;
using Microsoft.Extensions.Options;

namespace BryantThermo.Api.Services;

/// <summary>Request failed validation or the target doesn't exist; <see cref="Status"/> is the HTTP status to return.</summary>
public sealed class ControlException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Result of a control action: what the zone was set to and until when.</summary>
public sealed record ControlResult
{
    public required string ZoneId { get; init; }
    public double? HeatSetPoint { get; init; }
    public double? CoolSetPoint { get; init; }

    /// <summary>"HH:mm" thermostat-local time when the hold ends and the schedule resumes; null if indefinite or no hold.</summary>
    public string? HoldUntil { get; init; }
    public bool Hold { get; init; }
}

/// <summary>
/// Temporary set point changes. Carrier has no "adjust" call: a temporary change is the zone's manual activity
/// with new set points, held until the start of the next schedule period.
/// </summary>
public sealed class ThermostatControl(
    CarrierCloudClient client,
    ThermostatPoller poller,
    IOptions<CarrierOptions> options,
    TimeProvider time,
    ILogger<ThermostatControl> log)
{
    public const double MaxDelta = 10;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raises or lowers both of the zone's current set points by <paramref name="delta"/> until the next period.</summary>
    public Task<ControlResult> AdjustAsync(string zoneId, double delta, CancellationToken ct)
    {
        if (double.IsNaN(delta) || delta == 0 || Math.Abs(delta) > MaxDelta)
            throw new ControlException(400, $"delta must be non-zero and at most {MaxDelta} in magnitude.");
        return ApplyAsync(zoneId, ctx => (ctx.Heat + delta, ctx.Cool + delta), untilNextPeriod: true, ct);
    }

    /// <summary>Sets explicit set points. Holds until the next period unless <paramref name="indefinite"/>.</summary>
    public Task<ControlResult> SetAsync(string zoneId, double heat, double cool, bool indefinite, CancellationToken ct) =>
        ApplyAsync(zoneId, _ => (heat, cool), untilNextPeriod: !indefinite, ct);

    /// <summary>Cancels any hold; the program takes over again immediately.</summary>
    public async Task<ControlResult> ResumeAsync(string zoneId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var (serial, zone, _) = await LoadAsync(zoneId, ct);
            await client.ResumeScheduleAsync(serial, zone.Id, ct);
            await client.ReconcileAsync(ct);
            poller.RequestRefresh();
            return new ControlResult
            {
                ZoneId = zone.Id,
                HeatSetPoint = zone.HeatSetPoint,
                CoolSetPoint = zone.CoolSetPoint,
            };
        }
        finally { _gate.Release(); }
    }

    private async Task<ControlResult> ApplyAsync(
        string zoneId, Func<Context, (double Heat, double Cool)> compute, bool untilNextPeriod, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var (serial, zone, ctx) = await LoadAsync(zoneId, ct);
            var (heat, cool) = compute(ctx);
            Validate(heat, cool, ctx.Unit);

            var until = untilNextPeriod ? ScheduleHelper.NextPeriodStart(ctx.ZoneConfig, LocalNow()) : null;
            log.LogInformation("Zone {Zone}: setting {Heat}/{Cool} {Unit}, hold until {Until}.",
                zone.Id, heat, cool, ctx.Unit, until ?? "(indefinite)");

            await client.SetManualSetpointsAsync(serial, zone.Id, heat, cool, zone.FanMode, ct);
            await client.HoldManualAsync(serial, zone.Id, until, ct);
            await client.ReconcileAsync(ct);
            poller.RequestRefresh();

            return new ControlResult
            {
                ZoneId = zone.Id,
                HeatSetPoint = heat,
                CoolSetPoint = cool,
                HoldUntil = until,
                Hold = true,
            };
        }
        finally { _gate.Release(); }
    }

    private sealed record Context(double Heat, double Cool, string Unit, System.Text.Json.Nodes.JsonNode? ZoneConfig);

    // Always works from a fresh read so the change is relative to what the thermostat has right now.
    private async Task<(string Serial, ZoneStatus Zone, Context Ctx)> LoadAsync(string zoneId, CancellationToken ct)
    {
        var systems = await client.GetSystemsAsync(ct);
        var system = CarrierStatusMapper.SelectSystem(systems, options.Value.SystemSerial);
        var snapshot = CarrierStatusMapper.Map(system, time.GetUtcNow());

        var zone = snapshot.Zones.FirstOrDefault(z => string.Equals(z.Id, zoneId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ControlException(404, $"Zone '{zoneId}' not found.");
        if (!snapshot.Online)
            throw new ControlException(503, "The thermostat is offline.");

        var zoneConfig = (system["config"]?["zones"] as System.Text.Json.Nodes.JsonArray)?
            .FirstOrDefault(z => z?["id"]?.ToString() == zone.Id);
        return (snapshot.SystemSerial, zone,
            new Context(zone.HeatSetPoint ?? double.NaN, zone.CoolSetPoint ?? double.NaN, snapshot.TemperatureUnit, zoneConfig));
    }

    private static void Validate(double heat, double cool, string unit)
    {
        if (double.IsNaN(heat) || double.IsNaN(cool))
            throw new ControlException(409, "The zone's current set points are unknown.");
        var (min, max) = unit == "C" ? (7.0, 37.0) : (45.0, 99.0);
        if (heat < min || heat > max || cool < min || cool > max)
            throw new ControlException(400, $"Set points must be between {min} and {max} °{unit}.");
        if (heat >= cool)
            throw new ControlException(400, "The heat set point must be lower than the cool set point.");
    }

    private DateTime LocalNow()
    {
        var utc = time.GetUtcNow();
        if (string.IsNullOrWhiteSpace(options.Value.TimeZone)) return utc.ToLocalTime().DateTime;
        return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone)).DateTime;
    }
}
