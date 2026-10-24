using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BryantThermo.Api.Models;

namespace BryantThermo.Api.Carrier;

/// <summary>Turns Carrier's raw <c>infinitySystems</c> JSON into the backend's <see cref="ThermostatSnapshot"/>.</summary>
public static class CarrierStatusMapper
{
    /// <summary>
    /// Picks the requested system (or the first one when <paramref name="serial"/> is empty) and maps it.
    /// </summary>
    public static ThermostatSnapshot MapSystem(JsonArray systems, string? serial, DateTimeOffset fetchedAt)
    {
        if (systems.Count == 0)
            throw new CarrierApiException("No thermostat systems are registered on this Carrier/Bryant account.");

        JsonNode? system = string.IsNullOrWhiteSpace(serial)
            ? systems[0]
            : systems.FirstOrDefault(s =>
                string.Equals(s?["profile"]?["serial"]?.ToString(), serial, StringComparison.OrdinalIgnoreCase));

        if (system is null)
        {
            var available = string.Join(", ", systems.Select(s => s?["profile"]?["serial"]?.ToString()));
            throw new CarrierApiException($"System '{serial}' not found on the account. Available: {available}");
        }

        return Map(system, fetchedAt);
    }

    public static ThermostatSnapshot Map(JsonNode system, DateTimeOffset fetchedAt)
    {
        var profile = system["profile"];
        var status = system["status"] ?? throw new CarrierApiException("System has no status block.");

        // Zone names live in config; live readings live in status.
        var zoneNames = (system["config"]?["zones"] as JsonArray ?? [])
            .Where(z => z is not null)
            .ToDictionary(z => z!["id"]?.ToString() ?? "", z => z!["name"]?.ToString());

        var zones = (status["zones"] as JsonArray ?? [])
            .Where(z => z is not null && IsEnabled(z["enabled"]))
            .Select(z => MapZone(z!, zoneNames))
            .ToList();

        return new ThermostatSnapshot
        {
            SystemSerial = profile?["serial"]?.ToString() ?? "unknown",
            SystemName = profile?["name"]?.ToString(),
            Model = profile?["model"]?.ToString(),
            Online = !CarrierJson.GetBool(status["isDisconnected"]).GetValueOrDefault(),
            TemperatureUnit = status["cfgem"]?.ToString() is { Length: > 0 } unit ? unit.ToUpperInvariant() : "F",
            SystemMode = status["mode"]?.ToString(),
            IndoorUnitOperationalStatus = status["idu"]?["opstat"]?.ToString(),
            OutdoorUnitOperationalStatus = status["odu"]?["opstat"]?.ToString(),
            OutdoorTemperature = CarrierJson.GetDouble(status["oat"]),
            ThermostatTimeUtc = CarrierJson.GetTimestamp(status["utcTime"]),
            FetchedAtUtc = fetchedAt,
            Humidity = MapHumidity(status, system["config"], zones.FirstOrDefault()?.Activity),
            Zones = zones,
        };
    }

    public static HumiditySettings MapHumidity(JsonNode status, JsonNode? config, string? activity)
    {
        var home = MapHumidityProfile(config?["humidityHome"]);
        var away = MapHumidityProfile(config?["humidityAway"]);
        var vacation = MapHumidityProfile(config?["humidityVacation"]);

        var activeProfile = activity?.ToLowerInvariant() switch
        {
            "away" when away is not null => "away",
            "vacation" when vacation is not null => "vacation",
            _ => "home",
        };

        // status.humid is the humidifier's live on/off state; some systems omit it.
        var running = CarrierJson.GetBool(status["humid"]);

        return new HumiditySettings
        {
            HumidifierInstalled = CarrierJson.GetBool(config?["cfghumid"]),
            HumidifierRunning = running,
            HumidifierLevel = CarrierJson.GetDouble(status["humlvl"]) is { } lvl ? (int)Math.Round(lvl) : null,
            ActiveProfile = activeProfile,
            Active = activeProfile switch { "away" => away, "vacation" => vacation, _ => home },
            Home = home,
            Away = away,
            Vacation = vacation,
        };
    }

    private static HumidityProfile? MapHumidityProfile(JsonNode? p)
    {
        if (p is null) return null;
        return new HumidityProfile
        {
            Mode = p["humid"]?.ToString(),
            HumidifierEnabled = CarrierJson.GetBool(p["humidifier"]),
            HeatingTargetPercent = StepsToPercent(p["rhtg"]),
            CoolingTargetPercent = StepsToPercent(p["rclg"]),
            OverCool = CarrierJson.GetBool(p["rclgovercool"]),
        };
    }

    /// <summary>Carrier stores humidity targets in 5 % steps (rhtg = 8 means 40 %, rclg = 11 means 55 %).</summary>
    public static int? StepsToPercent(JsonNode? node) =>
        CarrierJson.GetDouble(node) is { } v ? (int)Math.Round(v * 5) : null;

    private static ZoneStatus MapZone(JsonNode z, IReadOnlyDictionary<string, string?> names)
    {
        var id = z["id"]?.ToString() ?? "";
        var conditioning = z["zoneconditioning"]?.ToString();
        var (action, active) = ParseConditioning(conditioning);

        return new ZoneStatus
        {
            Id = id,
            Name = names.GetValueOrDefault(id),
            Temperature = CarrierJson.GetDouble(z["rt"]),
            Humidity = CarrierJson.GetDouble(z["rh"]) is { } rh ? (int)Math.Round(rh) : null,
            HeatSetPoint = CarrierJson.GetDouble(z["htsp"]),
            CoolSetPoint = CarrierJson.GetDouble(z["clsp"]),
            Action = action,
            ActionActive = active,
            Conditioning = conditioning,
            FanMode = z["fan"]?.ToString(),
            Activity = z["currentActivity"]?.ToString(),
            Hold = string.Equals(z["hold"]?.ToString(), "on", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Carrier reports e.g. "active_heat", "prep_heat", "pending_heat", "active_cool", ..., "idle".
    /// </summary>
    public static (HvacAction Action, bool Active) ParseConditioning(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (HvacAction.Unknown, false);
        var v = value.Trim().ToLowerInvariant();
        if (v == "idle") return (HvacAction.Idle, false);

        var active = v.StartsWith("active", StringComparison.Ordinal);
        if (v.EndsWith("heat", StringComparison.Ordinal)) return (HvacAction.Heating, active);
        if (v.EndsWith("cool", StringComparison.Ordinal)) return (HvacAction.Cooling, active);
        return (HvacAction.Unknown, false);
    }

    private static bool IsEnabled(JsonNode? enabled) =>
        enabled is null || CarrierJson.GetBool(enabled) != false;
}

/// <summary>Lenient readers: Carrier sends most numbers and flags as strings ("71.0", "on").</summary>
public static class CarrierJson
{
    public static double? GetDouble(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.Number => v.GetValue<double>(),
            JsonValueKind.String when double.TryParse(v.GetValue<string>(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    public static bool? GetBool(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetValue<string>().Trim().ToLowerInvariant() switch
            {
                "on" or "true" or "yes" or "1" => true,
                "off" or "false" or "no" or "0" => false,
                _ => null,
            },
            _ => null,
        };
    }

    public static DateTimeOffset? GetTimestamp(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String &&
        DateTimeOffset.TryParse(v.GetValue<string>(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : null;
}
