using System.Text.Json.Nodes;
using BryantThermo.Api.Carrier;

namespace BryantThermo.Tests;

public class ScheduleHelperTests
{
    // Every day: wake 06:00, away 08:30 (disabled on Monday), home 17:00, sleep 22:00.
    private static JsonNode Zone()
    {
        var days = new JsonArray();
        for (var d = 0; d < 7; d++)
        {
            days.Add(new JsonObject
            {
                ["period"] = new JsonArray
                {
                    P("06:00", "on"), P("08:30", d == 1 ? "off" : "on"), P("17:00", "on"), P("22:00", "on"),
                },
            });
        }
        return new JsonObject { ["program"] = new JsonObject { ["day"] = days } };
    }

    private static JsonObject P(string time, string enabled) =>
        new() { ["time"] = time, ["enabled"] = enabled, ["activity"] = "home" };

    // 2026-11-01 is a Sunday.
    [Theory]
    [InlineData("2026-11-01T05:00", "06:00")]
    [InlineData("2026-11-01T06:00", "08:30")] // a period starting right now is the current one
    [InlineData("2026-11-01T12:15", "17:00")]
    [InlineData("2026-11-01T23:00", "06:00")] // rolls to next day's first period
    [InlineData("2026-11-02T07:00", "17:00")] // Monday's 08:30 is disabled
    public void NextPeriodStart_ReturnsNextEnabledPeriod(string now, string expected) =>
        Assert.Equal(expected, ScheduleHelper.NextPeriodStart(Zone(), DateTime.Parse(now)));

    [Fact]
    public void NormalizeTime_PadsCarrierValues()
    {
        Assert.Equal("06:00", ScheduleHelper.NormalizeTime("6:0"));
        Assert.Equal("17:05", ScheduleHelper.NormalizeTime("17:05"));
        Assert.Null(ScheduleHelper.NormalizeTime(null));
    }

    [Fact]
    public void NextPeriodStart_NullWithoutProgram() =>
        Assert.Null(ScheduleHelper.NextPeriodStart(new JsonObject(), DateTime.Now));
}
