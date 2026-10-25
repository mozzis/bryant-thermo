using BryantThermo.Api.Models;
using BryantThermo.Api.Services;

namespace BryantThermo.Tests;

public class PrometheusMetricsTests
{
    [Theory]
    [InlineData(HvacAction.Idle, false, "off")]
    [InlineData(HvacAction.Heating, true, "heating")]
    [InlineData(HvacAction.Cooling, true, "cooling")]
    [InlineData(HvacAction.Heating, false, "heating")]
    [InlineData(HvacAction.Cooling, false, "cooling")]
    public void Temperature_metric_reports_equipment_state(HvacAction action, bool active, string expected)
    {
        var metrics = PrometheusMetrics.Render(Snapshot(action, active, humidity: 40, target: 40));

        Assert.Contains($"thermostat_temperature{{unit=\"F\",equipment=\"{expected}\"}} 70.5", metrics);
    }

    [Theory]
    [InlineData(34, "Low")]
    [InlineData(35, "Normal")]
    [InlineData(40, "Normal")]
    [InlineData(45, "Normal")]
    [InlineData(46, "High")]
    public void Humidity_metric_uses_five_point_normal_band(int humidity, string expected)
    {
        var metrics = PrometheusMetrics.Render(Snapshot(HvacAction.Idle, false, humidity, target: 40));

        Assert.Contains($"thermostat_humidity{{state=\"{expected}\",humidifier=\"off\"}} {humidity}", metrics);
    }

    [Theory]
    [InlineData(true, "running")]
    [InlineData(false, "off")]
    [InlineData(null, "unknown")]
    public void Humidity_metric_reports_humidifier_running_state(bool? running, string expected)
    {
        var metrics = PrometheusMetrics.Render(Snapshot(HvacAction.Idle, false, humidity: 40, target: 40, running));

        Assert.Contains($"thermostat_humidity{{state=\"Normal\",humidifier=\"{expected}\"}} 40", metrics);
    }

    [Fact]
    public void Humidity_metric_marks_missing_setpoint_unavailable()
    {
        var metrics = PrometheusMetrics.Render(Snapshot(HvacAction.Idle, false, humidity: 40, target: null));

        Assert.Contains("thermostat_humidity{state=\"Setpoint unavailable\",humidifier=\"off\"} 40", metrics);
    }

    [Fact]
    public void Metrics_use_lf_line_endings()
    {
        var metrics = PrometheusMetrics.Render(Snapshot(HvacAction.Idle, false, humidity: 40, target: 40));

        Assert.DoesNotContain('\r', metrics);
    }

    private static ThermostatSnapshot Snapshot(HvacAction action, bool active, int? humidity, int? target, bool? humidifierRunning = false) => new()
    {
        SystemSerial = "test",
        TemperatureUnit = "F",
        Humidity = new HumiditySettings
        {
            ActiveProfile = "home",
            HumidifierRunning = humidifierRunning,
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