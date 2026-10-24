namespace BryantThermo.Api.Models;

/// <summary>
/// Compact view of the first (or only) zone: the readings and settings a dashboard needs.
/// </summary>
public sealed record ThermostatSummary
{
    /// <summary>Serial number of the reported system.</summary>
    public required string SystemSerial { get; init; }

    /// <summary>False when Carrier's cloud has lost contact with the thermostat.</summary>
    public bool Online { get; init; }

    /// <summary>"F" or "C" — the unit of every temperature in this response.</summary>
    public required string TemperatureUnit { get; init; }

    /// <summary>Current indoor temperature.</summary>
    public double? Temperature { get; init; }

    /// <summary>Current indoor relative humidity, %.</summary>
    public int? Humidity { get; init; }

    /// <summary>Heating set point.</summary>
    public double? HeatSetPoint { get; init; }

    /// <summary>Cooling set point.</summary>
    public double? CoolSetPoint { get; init; }

    /// <summary>What the equipment is doing: heating, cooling, idle or unknown.</summary>
    public HvacAction Action { get; init; }

    /// <summary>True when the equipment is running for <see cref="Action"/>; false while staging up or idle.</summary>
    public bool ActionActive { get; init; }

    /// <summary>
    /// True when the indoor blower is running. Taken from blower RPM/airflow when the system reports them,
    /// otherwise inferred from heating or cooling being active.
    /// </summary>
    public bool FanRunning { get; init; }

    /// <summary>Humidifier set point (%) in the active comfort profile; null when humidifying is off.</summary>
    public int? HumidityTarget { get; init; }

    /// <summary>True when humidifying is enabled in the active comfort profile.</summary>
    public bool? HumidifierEnabled { get; init; }

    /// <summary>True while the humidifier is running. Null if the system doesn't report it.</summary>
    public bool? HumidifierRunning { get; init; }

    /// <summary>When this backend fetched the data from Carrier (UTC).</summary>
    public DateTimeOffset FetchedAtUtc { get; init; }

    public static ThermostatSummary From(ThermostatSnapshot s)
    {
        var z = s.Zones.FirstOrDefault();
        var h = s.Humidity.Active;
        return new ThermostatSummary
        {
            SystemSerial = s.SystemSerial,
            Online = s.Online,
            TemperatureUnit = s.TemperatureUnit,
            Temperature = z?.Temperature,
            Humidity = z?.Humidity,
            HeatSetPoint = z?.HeatSetPoint,
            CoolSetPoint = z?.CoolSetPoint,
            Action = z?.Action ?? HvacAction.Unknown,
            ActionActive = z?.ActionActive ?? false,
            FanRunning = s.FanRunning ?? (z is { ActionActive: true }),
            HumidityTarget = h?.HumidifierEnabled == false ? null : h?.HeatingTargetPercent,
            HumidifierEnabled = h?.HumidifierEnabled,
            HumidifierRunning = s.Humidity.HumidifierRunning,
            FetchedAtUtc = s.FetchedAtUtc,
        };
    }
}

/// <summary>Backend and polling health.</summary>
public sealed record HealthResponse
{
    /// <summary>"ok" when fresh data is available; "degraded" when there is no data or it is stale.</summary>
    public required string Status { get; init; }

    /// <summary>When the last successful poll of Carrier happened (UTC).</summary>
    public DateTimeOffset? LastFetchUtc { get; init; }

    /// <summary>Age of the current data, in seconds.</summary>
    public double? AgeSeconds { get; init; }

    /// <summary>Whether Carrier reports the thermostat as connected.</summary>
    public bool? ThermostatOnline { get; init; }

    /// <summary>Message from the most recent failed poll; null once a poll succeeds.</summary>
    public string? LastError { get; init; }

    /// <summary>When the most recent failure happened (UTC).</summary>
    public DateTimeOffset? LastErrorAtUtc { get; init; }
}
