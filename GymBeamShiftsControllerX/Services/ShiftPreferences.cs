using System.Text.RegularExpressions;
using GymBeamShiftsControllerX.Models;

namespace GymBeamShiftsControllerX.Services
{
    public static class ShiftPreferences
    {
        public static string Normalize(string? value) =>
            Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

        public static bool IsAllowed(ShiftEntry shift, ShiftRulesSettings rules) =>
            !Contains(rules.ExcludedShiftTypes, shift.ShiftType)
            && !Contains(rules.ExcludedLocations, shift.Location);

        public static List<ShiftEntry> Prioritize(List<ShiftEntry>? shifts, ShiftRulesSettings rules)
        {
            if (shifts == null) return new List<ShiftEntry>();
            var types = Priorities(rules.PreferredShiftTypes);
            var locations = Priorities(rules.PreferredLocations);
            if (types.Count == 0 && locations.Count == 0) return shifts;

            // GroupBy preserves the first appearance of each date. OrderBy is stable.
            return shifts.GroupBy(shift => shift.Date.Date)
                .SelectMany(group => group
                    .OrderBy(shift => Rank(types, shift.ShiftType))
                    .ThenBy(shift => Rank(locations, shift.Location)))
                .ToList();
        }

        private static bool Contains(IEnumerable<string>? values, string value) =>
            values?.Any(item => string.Equals(Normalize(item), Normalize(value),
                StringComparison.OrdinalIgnoreCase)) == true;

        private static Dictionary<string, int> Priorities(IEnumerable<string>? values)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in values ?? Array.Empty<string>())
            {
                string normalized = Normalize(value);
                if (normalized.Length > 0) result.TryAdd(normalized, result.Count);
            }
            return result;
        }

        private static int Rank(Dictionary<string, int> priorities, string value) =>
            priorities.TryGetValue(Normalize(value), out int rank) ? rank : int.MaxValue;
    }
}
