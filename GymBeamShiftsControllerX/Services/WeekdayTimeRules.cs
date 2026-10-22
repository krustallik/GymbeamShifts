using System.Globalization;
using GymBeamShiftsControllerX.Models;

namespace GymBeamShiftsControllerX.Services;

public static class WeekdayTimeRules
{
    public static readonly IReadOnlyList<string> Weekdays = Array.AsReadOnly(new[]
        { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" });

    // A missing key inherits the base list; an explicit empty list blocks no times.
    public static IReadOnlyList<string> Resolve(ShiftRulesSettings rules, DateTime date, bool isHoliday)
    {
        if (!isHoliday && date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && rules.WeekdayStartTimesToSkip != null)
        {
            foreach (var (day, times) in rules.WeekdayStartTimesToSkip)
                if (string.Equals(day, date.DayOfWeek.ToString(), StringComparison.OrdinalIgnoreCase)
                    && times != null) return times;
        }
        return rules.StartTimesToSkip ?? new List<string>();
    }

    public static bool IsValid(Dictionary<string, List<string>>? overrides)
    {
        if (overrides == null) return true;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return overrides.Count <= 5 && overrides.All(entry =>
            Weekdays.Contains(entry.Key, StringComparer.OrdinalIgnoreCase)
            && seen.Add(entry.Key) && entry.Value != null && entry.Value.Count <= 100
            && entry.Value.All(time => TimeOnly.TryParseExact(time?.Trim(), "HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _)));
    }

    public static Dictionary<string, List<string>> Copy(Dictionary<string, List<string>>? overrides)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (day, times) in overrides ?? new())
        {
            string? canonical = Weekdays.FirstOrDefault(value => string.Equals(value, day, StringComparison.OrdinalIgnoreCase));
            if (canonical != null && times != null)
                result[canonical] = times.Where(time => !string.IsNullOrWhiteSpace(time))
                    .Select(time => time.Trim()).Distinct(StringComparer.Ordinal).ToList();
        }
        return result;
    }
}
