using System.Globalization;
using System.Text.RegularExpressions;

namespace GymBeamShiftsControllerX.Services
{
    public static class ShiftTimeParser
    {
        public static bool IsBlockedStart(string? value, IEnumerable<string>? blockedTimes)
        {
            if (!TimeOnly.TryParseExact(value?.Trim(), "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var start)) return false;

            foreach (string blocked in blockedTimes ?? Array.Empty<string>())
            {
                if (!TimeOnly.TryParseExact(blocked?.Trim(), "HH:mm", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var center)) continue;

                double distance = Math.Abs((start.ToTimeSpan() - center.ToTimeSpan()).TotalMinutes);
                if (Math.Min(distance, 1440 - distance) <= 30) return true;
            }
            return false;
        }

        public static bool TryParseRange(string? value, out string start, out string end)
        {
            start = end = string.Empty;
            var match = Regex.Match(value ?? string.Empty,
                @"^\s*(\d{2}:\d{2})\s*[-–—]\s*(\d{2}:\d{2})\s*$");
            if (!match.Success
                || !TimeOnly.TryParseExact(match.Groups[1].Value, "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var from)
                || !TimeOnly.TryParseExact(match.Groups[2].Value, "HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var to))
            {
                return false;
            }

            start = from.ToString("HH:mm", CultureInfo.InvariantCulture);
            end = to.ToString("HH:mm", CultureInfo.InvariantCulture);
            return true;
        }
    }
}
