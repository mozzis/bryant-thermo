using System.Globalization;
using System.Text;
using BryantThermo.Api.Models;

namespace BryantThermo.Api.Services;

public static class PrometheusMetrics
{
    public static string Render(ThermostatSnapshot? snapshot)
    {
        var output = new StringBuilder();
        output.Append("# HELP thermostat_temperature Current indoor temperature.\n");
        output.Append("# TYPE thermostat_temperature gauge\n");
        output.Append("# HELP thermostat_humidity Current indoor relative humidity in percent.\n");
        output.Append("# TYPE thermostat_humidity gauge\n");

        var zone = snapshot?.Zones.FirstOrDefault();
        if (snapshot is null || zone is null)
            return output.ToString();

        var equipment = zone.ActionActive ? zone.Action switch
        {
            HvacAction.Heating => "heating",
            HvacAction.Cooling => "cooling",
            _ => "off",
        } : "off";

        if (zone.Temperature is { } temperature && double.IsFinite(temperature))
        {
            output.Append("thermostat_temperature{unit=\"")
                .Append(EscapeLabel(snapshot.TemperatureUnit))
                .Append("\",equipment=\"").Append(equipment).Append("\"} ")
                .Append(temperature.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        if (zone.Humidity is { } humidity)
        {
            var target = snapshot.Humidity.Active?.HumidifierEnabled == false
                ? null
                : snapshot.Humidity.Active?.HeatingTargetPercent;
            var state = target is null
                ? "setpoint_unavailable"
                : humidity >= target.Value ? "at_or_above_setpoint" : "below_setpoint";

            output.Append("thermostat_humidity{state=\"").Append(state).Append("\"} ")
                .Append(humidity.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        return output.ToString();
    }

    private static string EscapeLabel(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}