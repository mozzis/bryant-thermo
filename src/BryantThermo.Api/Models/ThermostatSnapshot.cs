namespace BryantThermo.Api.Models;

/// <summary>What the HVAC equipment is doing right now in a zone.</summary>
public enum HvacAction
{
    Unknown,
    Idle,
    Heating,
    Cooling,
}

/// <summary>A point-in-time view of the whole thermostat system, as served to the front end.</summary>
public sealed record ThermostatSnapshot
{
    public required string SystemSerial { get; init; }
    public string? SystemName { get; init; }
    public string? Model { get; init; }

    /// <summary>False when Carrier's cloud reports it has lost contact with the thermostat.</summary>
    public bool Online { get; init; }

    /// <summary>"F" or "C" — the unit all temperatures in this snapshot are expressed in.</summary>
    public required string TemperatureUnit { get; init; }

    /// <summary>System mode as Carrier reports it (e.g. "heat", "cool", "auto", "off", "gasheat", "hpheat").</summary>
    public string? SystemMode { get; init; }

    /// <summary>Carrier's raw indoor-unit operational status (<c>idu.opstat</c>).</summary>
    public string? IndoorUnitOperationalStatus { get; init; }

    /// <summary>Carrier's raw outdoor-unit operational status (<c>odu.opstat</c>).</summary>
    public string? OutdoorUnitOperationalStatus { get; init; }

    public double? OutdoorTemperature { get; init; }

    /// <summary>Timestamp the thermostat attached to this status (UTC).</summary>
    public DateTimeOffset? ThermostatTimeUtc { get; init; }

    /// <summary>When this backend fetched the snapshot from Carrier (UTC).</summary>
    public DateTimeOffset FetchedAtUtc { get; init; }

    /// <summary>Humidifier configuration and state (system-wide; Carrier doesn't set humidity per zone).</summary>
    public required HumiditySettings Humidity { get; init; }

    public required IReadOnlyList<ZoneStatus> Zones { get; init; }
}

public sealed record HumiditySettings
{
    /// <summary>True when a humidifier is configured as installed on the system.</summary>
    public bool? HumidifierInstalled { get; init; }

    /// <summary>True while the humidifier is actually running right now. Null if the system doesn't report it.</summary>
    public bool? HumidifierRunning { get; init; }

    /// <summary>Carrier's reported humidifier output level ("humlvl"), passed through unconverted.</summary>
    public int? HumidifierLevel { get; init; }

    /// <summary>
    /// Which profile is in effect: "away" while the first zone's activity is away, "vacation" during vacation,
    /// otherwise "home" (sleep, wake and manual activities use the home humidity settings).
    /// </summary>
    public required string ActiveProfile { get; init; }

    /// <summary>The settings of <see cref="ActiveProfile"/> — i.e. what controls the humidifier right now.</summary>
    public HumidityProfile? Active { get; init; }

    public HumidityProfile? Home { get; init; }
    public HumidityProfile? Away { get; init; }
    public HumidityProfile? Vacation { get; init; }
}

public sealed record HumidityProfile
{
    /// <summary>Humidify mode as Carrier reports it: typically "off", "manual" or "auto".</summary>
    public string? Mode { get; init; }

    /// <summary>True when humidifying is enabled in this profile.</summary>
    public bool? HumidifierEnabled { get; init; }

    /// <summary>Target relative humidity (%) while heating — the humidifier setpoint. 5–45 in steps of 5.</summary>
    public int? HeatingTargetPercent { get; init; }

    /// <summary>Target relative humidity (%) for dehumidifying while cooling.</summary>
    public int? CoolingTargetPercent { get; init; }

    /// <summary>True when the system may cool below the set point to remove moisture ("over-cool").</summary>
    public bool? OverCool { get; init; }
}

public sealed record ZoneStatus
{
    public required string Id { get; init; }
    public string? Name { get; init; }

    public double? Temperature { get; init; }
    public int? Humidity { get; init; }
    public double? HeatSetPoint { get; init; }
    public double? CoolSetPoint { get; init; }

    /// <summary>Heating, Cooling, Idle (or Unknown if Carrier sends something unrecognised).</summary>
    public HvacAction Action { get; init; }

    /// <summary>
    /// True when the equipment is actually running for <see cref="Action"/>; false while it is
    /// staging up ("prep_*" / "pending_*" in Carrier's terms) or idle.
    /// </summary>
    public bool ActionActive { get; init; }

    /// <summary>Carrier's raw zone conditioning value, e.g. "active_heat", "pending_cool", "idle".</summary>
    public string? Conditioning { get; init; }

    /// <summary>Configured fan setting for the current activity: "off" (auto), "low", "med", "high".</summary>
    public string? FanMode { get; init; }

    /// <summary>Current comfort activity: "home", "away", "sleep", "wake", "manual", "vacation".</summary>
    public string? Activity { get; init; }

    public bool Hold { get; init; }
}
