using System.Collections.Generic;
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
                _current = new ShiftRulesSettings
                {
                    IncludedWeekdays = CleanList(update.IncludedWeekdays),
                    StartTimesToSkip = CleanList(update.StartTimesToSkip),
                    Holidays = CleanList(update.Holidays),
                    ExcludedDates = CleanList(update.ExcludedDates),
                    FavoriteShiftUsers = CleanList(update.FavoriteShiftUsers)
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
                IncludedWeekdays = new List<string>(source.IncludedWeekdays ?? new List<string>()),
                StartTimesToSkip = new List<string>(source.StartTimesToSkip ?? new List<string>()),
                Holidays = new List<string>(source.Holidays ?? new List<string>()),
                ExcludedDates = new List<string>(source.ExcludedDates ?? new List<string>()),
                FavoriteShiftUsers = new List<string>(source.FavoriteShiftUsers ?? new List<string>())
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
    }
}
