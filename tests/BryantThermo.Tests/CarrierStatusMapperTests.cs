using System.Text.Json.Nodes;
using BryantThermo.Api.Carrier;
using BryantThermo.Api.Models;

namespace BryantThermo.Tests;

public class CarrierStatusMapperTests
{
    // Shape of Carrier's infinitySystems response (values are strings, as Carrier sends them).
    private const string SampleJson = """
        [
          {
            "profile": { "serial": "1234W567890", "name": "My Home", "model": "SYSTXBBECW01-A", "brand": "BRYANT" },
            "status": {
              "utcTime": "2026-10-24T14:40:00Z",
              "isDisconnected": false,
              "cfgem": "F",
              "mode": "heat",
              "oat": "48",
              "idu": { "opstat": "on", "cfm": "820", "blwrpm": "640" },
              "odu": { "opstat": "off" },
              "humid": "on",
              "humlvl": "2",
              "zones": [
                { "id": "1", "rt": "68.5", "rh": "41", "fan": "off", "htsp": "69", "clsp": "76",
                  "hold": "off", "enabled": "on", "currentActivity": "home", "zoneconditioning": "active_heat" },
                { "id": "2", "rt": "70", "rh": "40", "fan": "off", "htsp": "65", "clsp": "80",
                  "hold": "on", "enabled": "off", "currentActivity": "away", "zoneconditioning": "idle" }
              ]
            },
            "config": {
              "cfghumid": "on",
              "humidityHome": { "humid": "manual", "humidifier": "on", "rhtg": "7", "rclg": "11", "rclgovercool": "off" },
              "humidityAway": { "humid": "off", "humidifier": "off", "rhtg": "5", "rclg": "12", "rclgovercool": "on" },
              "humidityVacation": null,
              "zones": [ { "id": "1", "name": "Main Floor", "enabled": "on" } ]
            }
          }
        ]
        """;

    private static readonly DateTimeOffset Now = new(2026, 10, 24, 14, 41, 0, TimeSpan.Zero);

    [Fact]
    public void Maps_readings_setpoints_and_action()
    {
        var snap = CarrierStatusMapper.MapSystem(JsonNode.Parse(SampleJson)!.AsArray(), null, Now);

        Assert.Equal("1234W567890", snap.SystemSerial);
        Assert.True(snap.Online);
        Assert.Equal("F", snap.TemperatureUnit);
        Assert.Equal(48, snap.OutdoorTemperature);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 14, 40, 0, TimeSpan.Zero), snap.ThermostatTimeUtc);

        var zone = Assert.Single(snap.Zones); // disabled zone 2 is dropped
        Assert.Equal("Main Floor", zone.Name);
        Assert.Equal(68.5, zone.Temperature);
        Assert.Equal(41, zone.Humidity);
        Assert.Equal(69, zone.HeatSetPoint);
        Assert.Equal(76, zone.CoolSetPoint);
        Assert.Equal(HvacAction.Heating, zone.Action);
        Assert.True(zone.ActionActive);
        Assert.Equal("home", zone.Activity);
        Assert.False(zone.Hold);
    }

    [Fact]
    public void Maps_humidifier_settings_for_home_activity()
    {
        var h = CarrierStatusMapper.MapSystem(JsonNode.Parse(SampleJson)!.AsArray(), null, Now).Humidity;

        Assert.True(h.HumidifierInstalled);
        Assert.True(h.HumidifierRunning);
        Assert.Equal(2, h.HumidifierLevel);
        Assert.Equal("home", h.ActiveProfile);
        Assert.Equal("manual", h.Active!.Mode);
        Assert.True(h.Active.HumidifierEnabled);
        Assert.Equal(35, h.Active.HeatingTargetPercent);   // rhtg 7 x 5 %
        Assert.Equal(55, h.Active.CoolingTargetPercent);   // rclg 11 x 5 %
        Assert.False(h.Active.OverCool);
        Assert.False(h.Away!.HumidifierEnabled);
        Assert.Null(h.Vacation);
    }

    [Theory]
    [InlineData("away", "away", false)]
    [InlineData("sleep", "home", true)]
    [InlineData("vacation", "home", true)] // no vacation profile reported -> falls back to home
    public void Picks_profile_from_activity(string activity, string expected, bool enabled)
    {
        var sys = JsonNode.Parse(SampleJson)!.AsArray()[0]!;
        var h = CarrierStatusMapper.MapHumidity(sys["status"]!, sys["config"], activity);
        Assert.Equal(expected, h.ActiveProfile);
        Assert.Equal(enabled, h.Active!.HumidifierEnabled);
    }

    [Theory]
    [InlineData("8", 40)]
    [InlineData("0", 0)]
    [InlineData("12", 60)]
    [InlineData(null, null)]
    public void Converts_humidity_steps(string? raw, int? percent)
    {
        Assert.Equal(percent, CarrierStatusMapper.StepsToPercent(raw is null ? null : JsonValue.Create(raw)));
    }

    [Theory]
    [InlineData("active_heat", HvacAction.Heating, true)]
    [InlineData("prep_heat", HvacAction.Heating, false)]
    [InlineData("pending_cool", HvacAction.Cooling, false)]
    [InlineData("active_cool", HvacAction.Cooling, true)]
    [InlineData("idle", HvacAction.Idle, false)]
    [InlineData("something_new", HvacAction.Unknown, false)]
    [InlineData(null, HvacAction.Unknown, false)]
    public void Parses_zone_conditioning(string? raw, HvacAction action, bool active)
    {
        Assert.Equal((action, active), CarrierStatusMapper.ParseConditioning(raw));
    }

    [Fact]
    public void Unknown_serial_lists_available_systems()
    {
        var ex = Assert.Throws<CarrierApiException>(() =>
            CarrierStatusMapper.MapSystem(JsonNode.Parse(SampleJson)!.AsArray(), "NOPE", Now));
        Assert.Contains("1234W567890", ex.Message);
    }
}
