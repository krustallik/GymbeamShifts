using System.Text.Json;
using GymBeamShiftsControllerX.Models;
using GymBeamShiftsControllerX.Services;

namespace GymBeamShiftsControllerX.Tests;

public class WeekdayTimeRulesTests
{
    [Fact]
    public void MissingOverrideInheritsCurrentBaseButCustomAndEmptyRemainIndependent()
    {
        var rules = new ShiftRulesSettings
        {
            StartTimesToSkip = new() { "22:00" },
            WeekdayStartTimesToSkip = new() { ["Monday"] = new() { "22:00" }, ["Tuesday"] = new() }
        };
        var monday = new DateTime(2026, 11, 2);
        Assert.Equal(new[] { "22:00" }, WeekdayTimeRules.Resolve(rules, monday.AddDays(2), false));
        rules.StartTimesToSkip = new() { "08:00" };
        Assert.Equal(new[] { "22:00" }, WeekdayTimeRules.Resolve(rules, monday, false));
        Assert.Empty(WeekdayTimeRules.Resolve(rules, monday.AddDays(1), false));
        Assert.Equal(new[] { "08:00" }, WeekdayTimeRules.Resolve(rules, monday.AddDays(2), false));
        rules.WeekdayStartTimesToSkip.Remove("Monday");
        Assert.Equal(new[] { "08:00" }, WeekdayTimeRules.Resolve(rules, monday, false));
    }

    [Theory]
    [InlineData("2026-11-02", true)]
    [InlineData("2026-11-07", false)]
    [InlineData("2026-11-08", false)]
    public void HolidaysAndWeekendsAlwaysUseBase(string date, bool holiday)
    {
        var day = DateTime.Parse(date);
        var rules = new ShiftRulesSettings
        {
            StartTimesToSkip = new() { "14:00" },
            WeekdayStartTimesToSkip = new() { [day.DayOfWeek.ToString()] = new() { "22:00" } }
        };
        Assert.Equal(new[] { "14:00" }, WeekdayTimeRules.Resolve(rules, day, holiday));
    }

    [Theory]
    [InlineData("13:29", false)]
    [InlineData("13:30", true)]
    [InlineData("14:30", true)]
    [InlineData("14:31", false)]
    public void OverrideUsesInclusiveThirtyMinuteWindow(string start, bool blocked)
    {
        var rules = new ShiftRulesSettings
        {
            StartTimesToSkip = new() { "22:00" },
            WeekdayStartTimesToSkip = new() { ["Monday"] = new() { "14:00" } }
        };
        Assert.Equal(blocked, ShiftTimeParser.IsBlockedStart(start, WeekdayTimeRules.Resolve(rules, new(2026, 11, 2), false)));
    }

    [Fact]
    public void SnapshotsUpdatesAndCallerListsAreIsolatedAndDisabledDayRetainsItsList()
    {
        var values = new List<string> { " 14:00 ", "14:00" };
        var store = new ShiftRulesStore(new());
        var updated = store.Update(new()
        {
            IncludedWeekdays = new() { "Monday" },
            WeekdayStartTimesToSkip = new() { ["monday"] = values }
        });
        values.Clear(); updated.WeekdayStartTimesToSkip["Monday"].Clear();
        Assert.Equal(new[] { "14:00" }, store.GetSnapshot().WeekdayStartTimesToSkip["Monday"]);
        store.Update(new() { IncludedWeekdays = new() { "Friday" }, StartTimesToSkip = new() { "22:00" } });
        var saved = store.GetSnapshot();
        Assert.Equal(new[] { "14:00" }, saved.WeekdayStartTimesToSkip["Monday"]);
        Assert.Equal(new[] { "Friday" }, saved.IncludedWeekdays);
        store.Update(new() { WeekdayStartTimesToSkip = new() });
        Assert.Empty(store.GetSnapshot().WeekdayStartTimesToSkip);
    }

    [Fact]
    public void LegacyConfigInheritsAndJsonRoundTripPreservesExplicitEmptyList()
    {
        var legacy = JsonSerializer.Deserialize<ShiftRulesSettings>("{\"StartTimesToSkip\":[\"22:00\"]}")!;
        Assert.Empty(legacy.WeekdayStartTimesToSkip);
        Assert.Equal(new[] { "22:00" }, WeekdayTimeRules.Resolve(legacy, new(2026, 11, 2), false));
        legacy.WeekdayStartTimesToSkip["Monday"] = new();
        var roundTrip = JsonSerializer.Deserialize<ShiftRulesSettings>(JsonSerializer.Serialize(legacy))!;
        Assert.True(roundTrip.WeekdayStartTimesToSkip.ContainsKey("Monday"));
        Assert.Empty(WeekdayTimeRules.Resolve(roundTrip, new(2026, 11, 2), false));
    }

    [Theory]
    [InlineData("Saturday", "14:00")]
    [InlineData("Funday", "14:00")]
    [InlineData("1", "14:00")]
    [InlineData("Monday", "24:00")]
    [InlineData("Monday", "6:00")]
    [InlineData("Monday", "invalid")]
    public void RejectInvalidDayOrTime(string day, string time) =>
        Assert.False(WeekdayTimeRules.IsValid(new() { [day] = new() { time } }));

    [Fact]
    public void ValidationAcceptsEmptyAndRejectsNullDuplicateOrOversizedLists()
    {
        Assert.True(WeekdayTimeRules.IsValid(null));
        Assert.True(WeekdayTimeRules.IsValid(new() { ["Monday"] = new() }));
        Assert.True(WeekdayTimeRules.IsValid(new() { ["monday"] = new() { "06:00" } }));
        Assert.False(WeekdayTimeRules.IsValid(new() { ["Monday"] = null! }));
        Assert.False(WeekdayTimeRules.IsValid(new() { ["Monday"] = new() { null! } }));
        Assert.False(WeekdayTimeRules.IsValid(new() { ["Monday"] = new(), ["monday"] = new() }));
        Assert.False(WeekdayTimeRules.IsValid(new() { ["Monday"] = Enumerable.Repeat("14:00", 101).ToList() }));
    }
}
