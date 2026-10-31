using System.Globalization;
using System.Text.Json.Nodes;

namespace BryantThermo.Api.Carrier;

/// <summary>Reads a Carrier zone program (<c>config.zones[].program</c>) to find schedule period boundaries.</summary>
public static class ScheduleHelper
{
    /// <summary>
    /// Returns the start time ("HH:mm") of the first enabled period after <paramref name="localNow"/>, looking at
    /// the rest of today and then up to a week ahead. Null if the zone has no enabled periods.
    /// Carrier's program has seven days, index 0 = Sunday.
    /// </summary>
    public static string? NextPeriodStart(JsonNode? zoneConfig, DateTime localNow)
    {
        if (zoneConfig?["program"]?["day"] is not JsonArray days || days.Count < 7) return null;

        var nowMinutes = localNow.Hour * 60 + localNow.Minute;
        for (var offset = 0; offset <= 7; offset++)
        {
            var day = days[((int)localNow.DayOfWeek + offset) % 7];
            var times = (day?["period"] as JsonArray ?? [])
                .Where(p => CarrierJson.GetBool(p?["enabled"]) == true)
                .Select(p => ParseTime(p?["time"]?.ToString()))
                .OfType<int>()
                .Order();

            foreach (var t in times)
                if (offset > 0 || t > nowMinutes)
                    return $"{t / 60:00}:{t % 60:00}";
        }
        return null;
    }

    private static int? ParseTime(string? value) =>
        TimeSpan.TryParseExact(value, @"h\:mm", CultureInfo.InvariantCulture, out var t) ? (int)t.TotalMinutes : null;
}
