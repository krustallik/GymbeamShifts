using System;
using System.Collections.Generic;
using System.Globalization;
using GymBeamShiftsControllerX.Models;

namespace GymBeamShiftsControllerX.Services
{
    public class ShiftRulesStore
    {
        private readonly object _lock = new object();
        private ShiftRulesSettings _current;

        public ShiftRulesStore(ShiftRulesSettings initial)
        {
            _current = Clone(initial);
        }

        public ShiftRulesSettings GetSnapshot()
        {
            lock (_lock)
            {
                return Clone(_current);
            }
        }

        public ShiftRulesSettings Update(ShiftRulesUpdateRequest update)
        {
            lock (_lock)
            {
                var startTimesToSkip = CleanList(update.StartTimesToSkip);
                _current = new ShiftRulesSettings
                {
                    TakeLunch = update.TakeLunch,
                    IncludedWeekdays = CleanList(update.IncludedWeekdays),
                    StartTimesToSkip = startTimesToSkip,
                    StartTimesToSkipOnWeekendsAndHolidays = CleanSubset(
                        update.StartTimesToSkipOnWeekendsAndHolidays,
                        startTimesToSkip),
                    Holidays = CleanList(update.Holidays),
                    ExcludedDates = CleanList(update.ExcludedDates),
                    FavoriteShiftUsers = CleanList(update.FavoriteShiftUsers),
                    TargetShiftDateTime = CleanTargetShiftDateTime(update.TargetShiftDateTime)
                };

                return Clone(_current);
            }
        }

        private static ShiftRulesSettings Clone(ShiftRulesSettings source)
        {
            if (source == null)
            {
                return new ShiftRulesSettings();
            }

            return new ShiftRulesSettings
            {
                TakeLunch = source.TakeLunch,
                IncludedWeekdays = new List<string>(source.IncludedWeekdays ?? new List<string>()),
                StartTimesToSkip = new List<string>(source.StartTimesToSkip ?? new List<string>()),
                StartTimesToSkipOnWeekendsAndHolidays = new List<string>(
                    source.StartTimesToSkipOnWeekendsAndHolidays ?? new List<string>()),
                Holidays = new List<string>(source.Holidays ?? new List<string>()),
                ExcludedDates = new List<string>(source.ExcludedDates ?? new List<string>()),
                FavoriteShiftUsers = new List<string>(source.FavoriteShiftUsers ?? new List<string>()),
                TargetShiftDateTime = source.TargetShiftDateTime ?? string.Empty
            };
        }

        private static List<string> CleanList(List<string> source)
        {
            if (source == null)
            {
                return new List<string>();
            }

            var result = new List<string>();
            var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var value in source)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var trimmed = value.Trim();
                if (set.Add(trimmed))
                {
                    result.Add(trimmed);
                }
            }

            return result;
        }

        private static List<string> CleanSubset(List<string> source, List<string> allowedValues)
        {
            var selectedValues = new HashSet<string>(
                CleanList(source),
                System.StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var value in allowedValues)
            {
                if (selectedValues.Contains(value))
                {
                    result.Add(value);
                }
            }

            return result;
        }

        private static string CleanTargetShiftDateTime(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return DateTime.TryParseExact(
                value.Trim(),
                "yyyy-MM-dd'T'HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsed)
                ? parsed.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture)
                : string.Empty;
        }
    }
}
