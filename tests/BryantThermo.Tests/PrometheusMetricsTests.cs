using BryantThermo.Api.Models;
using BryantThermo.Api.Services;

namespace BryantThermo.Tests;

public class PrometheusMetricsTests
{
    [Theory]
    [InlineData(HvacAction.Idle, false, "off")]
    [InlineData(HvacAction.Heating, true, "heating")]
    [InlineData(HvacAction.Cooling, true, "cooling")]
    [InlineData(HvacAction.Heating, false, "off")]
    public void Temperature_metric_reports_only_active_hvac_state(HvacAction action, bool active, string expected)
    {
        var metrics = PrometheusMetrics.Render(Snapshot(action, active, humidity: 40, target: 40));

        Assert.Contains($"thermostat_temperature{{unit=\"F\",equipment=\"{expected}\"}} 70.5", metrics);
    }

    [Theory]
    [InlineData(39, "below_setpoint")]
    [InlineData(40, "at_or_above_setpoint")]
    [InlineData(41, "at_or_above_setpoint")]
    public void Humidity_metric_compares_inclusively_to_active_setpoint(int humidity, string expected)
    {
        var metrics = PrometheusMetrics.Render(Snapshot(HvacAction.Idle, false, humidity, target: 40));

        Assert.Contains($"thermostat_humidity{{state=\"{expected}\"}} {humidity}", metrics);
    }

    [Fact]
    public void Humidity_metric_marks_missing_setpoint_unavailable()
    {
        var metrics = PrometheusMetrics.Render(Snapshot(HvacAction.Idle, false, humidity: 40, target: null));

        Assert.Contains("thermostat_humidity{state=\"setpoint_unavailable\"} 40", metrics);
    }

    [Fact]
    public void Metrics_use_lf_line_endings()
    {
        var metrics = PrometheusMetrics.Render(Snapshot(HvacAction.Idle, false, humidity: 40, target: 40));

        Assert.DoesNotContain('\r', metrics);
    }

    private static ThermostatSnapshot Snapshot(HvacAction action, bool active, int? humidity, int? target) => new()
    {
        SystemSerial = "test",
        TemperatureUnit = "F",
        Humidity = new HumiditySettings
        {
            ActiveProfile = "home",
            Active = new HumidityProfile
            {
                HumidifierEnabled = true,
                HeatingTargetPercent = target,
            },
        },
        Zones =
        [
            new ZoneStatus
            {
                Id = "1",
                Temperature = 70.5,
                Humidity = humidity,
                Action = action,
                ActionActive = active,
            },
        ],
    };
}