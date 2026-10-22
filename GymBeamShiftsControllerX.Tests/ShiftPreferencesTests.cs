using System.Text.Json;
using GymBeamShiftsControllerX.Models;
using GymBeamShiftsControllerX.Services;

namespace GymBeamShiftsControllerX.Tests;

public class ShiftPreferencesTests
{
    [Theory]
    [InlineData("13:29", "14:00", false)]
    [InlineData("13:30", "14:00", true)]
    [InlineData("14:00", "14:00", true)]
    [InlineData("14:01", "14:00", true)]
    [InlineData("14:02", "14:00", true)]
    [InlineData("14:30", "14:00", true)]
    [InlineData("14:31", "14:00", false)]
    [InlineData("23:30", "00:00", true)]
    [InlineData("23:29", "00:00", false)]
    [InlineData("00:30", "00:00", true)]
    [InlineData("00:31", "00:00", false)]
    [InlineData("00:10", "23:45", true)]
    [InlineData("23:40", "00:10", true)]
    [InlineData("23:39", "00:10", false)]
    [InlineData("22:00", "06:00", false)]
    [InlineData("invalid", "14:00", false)]
    [InlineData("14:00", "invalid", false)]
    public void BlockedStart_IncludesThirtyMinuteBoundariesAndWrapsMidnight(string start, string blocked, bool expected)
    {
        Assert.Equal(expected, ShiftTimeParser.IsBlockedStart(start, new[] { blocked }));
    }

    [Fact]
    public void BlockedStart_EmptyListsDoNotBlock_AnyMatchingWindowBlocks()
    {
        Assert.False(ShiftTimeParser.IsBlockedStart("14:00", null));
        Assert.False(ShiftTimeParser.IsBlockedStart("14:00", Array.Empty<string>()));
        Assert.True(ShiftTimeParser.IsBlockedStart("14:02", new[] { "06:00", "invalid", "14:00" }));
    }

    [Theory]
    [InlineData("06:00 - 14:00", "06:00", "14:00")]
    [InlineData("06:00 - 14:01", "06:00", "14:01")]
    [InlineData("06:00 - 14:15", "06:00", "14:15")]
    [InlineData("08:00 - 16:00", "08:00", "16:00")]
    [InlineData("10:00 - 18:00", "10:00", "18:00")]
    [InlineData("14:00 - 22:00", "14:00", "22:00")]
    [InlineData("14:01 - 22:00", "14:01", "22:00")]
    [InlineData("14:02 - 22:00", "14:02", "22:00")]
    [InlineData("16:00 - 22:00", "16:00", "22:00")]
    [InlineData("22:00 - 06:00", "22:00", "06:00")]
    [InlineData(" 06:00\u00a0–\u00a014:00 ", "06:00", "14:00")]
    [InlineData("14:01—22:00", "14:01", "22:00")]
    public void TimeRange_ParsesActualFormatsIncludingOvernight(string value, string start, string end)
    {
        Assert.True(ShiftTimeParser.TryParseRange(value, out string from, out string to));
        Assert.Equal(start, from);
        Assert.Equal(end, to);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("06:00")]
    [InlineData("6:00 - 14:00")]
    [InlineData("24:00 - 06:00")]
    [InlineData("14:60 - 22:00")]
    [InlineData("06:00 - 25:00")]
    [InlineData("prefix 06:00 - 14:00")]
    [InlineData("06:00 - 14:00 extra")]
    public void TimeRange_RejectsMalformedValues(string? value)
    {
        Assert.False(ShiftTimeParser.TryParseRange(value, out string from, out string to));
        Assert.Empty(from);
        Assert.Empty(to);
    }

    [Theory]
    [InlineData("CTP2 Export", "Rastislavova", false)]
    [InlineData("  ctp2   EXPORT ", "Rastislavova", false)]
    [InlineData("RAS Výroba", "CTP2 - whs - letisko", false)]
    [InlineData("RAS Výroba", "  CTP2  - whs - letisko ", false)]
    [InlineData("RAS Výroba", "Rastislavova", true)]
    [InlineData("CTP2 Export extra", "Rastislavova", true)]
    public void Exclusions_OverridePreferencesAndMatchFullNormalizedName(string type, string location, bool expected)
    {
        var rules = new ShiftRulesSettings
        {
            PreferredShiftTypes = new() { "CTP2 Export" },
            PreferredLocations = new() { "CTP2 - whs - letisko" },
            ExcludedShiftTypes = new() { "CTP2 Export" },
            ExcludedLocations = new() { "CTP2 - whs - letisko" }
        };
        Assert.Equal(expected, ShiftPreferences.IsAllowed(new() { ShiftType = type, Location = location }, rules));
    }

    [Fact]
    public void Priorities_TypeThenLocationWithinDate_KeepTiesStableAndKeepOtherTypes()
    {
        DateTime first = new(2026, 11, 3), second = first.AddDays(-1);
        var shifts = new List<ShiftEntry>
        {
            new() { Date = first, ShiftType = "Other", Location = "Rastislavova", ShiftIdentifier = "other" },
            new() { Date = first, ShiftType = "B2B - CTP2", Location = "Rastislavova", ShiftIdentifier = "second-type" },
            new() { Date = first, ShiftType = "CTP2 Export", Location = "Unknown", ShiftIdentifier = "first-type" },
            new() { Date = second, ShiftType = "CTP2 Export", Location = "Rastislavova", ShiftIdentifier = "other-date" },
            new() { Date = first, ShiftType = " ctp2  EXPORT ", Location = " rastislavova ", ShiftIdentifier = "first-place" },
            new() { Date = first, ShiftType = "CTP2 Export", Location = "Rastislavova", ShiftIdentifier = "equal-tie" }
        };
        var rules = new ShiftRulesSettings
        {
            PreferredShiftTypes = new() { "CTP2 Export", "B2B - CTP2" },
            PreferredLocations = new() { "Rastislavova" }
        };
        var result = ShiftPreferences.Prioritize(shifts, rules);
        Assert.Equal(new[] { "first-place", "equal-tie", "first-type", "second-type", "other", "other-date" },
            result.Select(shift => shift.ShiftIdentifier));
        Assert.Equal("other", shifts[0].ShiftIdentifier);
    }

    [Fact]
    public void LocationPriority_WorksAloneAndForSameType()
    {
        var shifts = new List<ShiftEntry>
        {
            new() { ShiftType = "CTP2 Export", Location = "CTP2 - whs - letisko" },
            new() { ShiftType = "CTP2 Export", Location = "Rastislavova" }
        };
        var result = ShiftPreferences.Prioritize(shifts, new() { PreferredLocations = new() { "Rastislavova" } });
        Assert.Same(shifts[1], result[0]);
    }

    [Fact]
    public void EmptyOrNullLists_AllowAllAndRetainOriginalOrder()
    {
        var rules = new ShiftRulesSettings
        {
            PreferredShiftTypes = null!,
            PreferredLocations = null!,
            ExcludedShiftTypes = null!,
            ExcludedLocations = null!
        };
        var shifts = new List<ShiftEntry> { new() };
        Assert.True(ShiftPreferences.IsAllowed(shifts[0], rules));
        Assert.Same(shifts, ShiftPreferences.Prioritize(shifts, rules));
        Assert.Empty(ShiftPreferences.Prioritize(null, rules));
    }

    [Fact]
    public void Store_NormalizesDeduplicatesAndClonesAllFourLists()
    {
        var store = new ShiftRulesStore(new());
        var update = new ShiftRulesUpdateRequest
        {
            PreferredShiftTypes = new() { " CTP2  Export ", "ctp2 export", "", "RAS Výroba" },
            ExcludedShiftTypes = new() { " Olepovanie B2B " },
            PreferredLocations = new() { " Rastislavova ", "RASTISLAVOVA" },
            ExcludedLocations = new() { " CTP2 - whs - letisko " }
        };
        var saved = store.Update(update);
        update.PreferredShiftTypes.Clear();
        saved.PreferredLocations.Clear();
        var snapshot = store.GetSnapshot();
        Assert.Equal(new[] { "CTP2 Export", "RAS Výroba" }, snapshot.PreferredShiftTypes);
        Assert.Equal(new[] { "Olepovanie B2B" }, snapshot.ExcludedShiftTypes);
        Assert.Equal(new[] { "Rastislavova" }, snapshot.PreferredLocations);
        Assert.Equal(new[] { "CTP2 - whs - letisko" }, snapshot.ExcludedLocations);
        snapshot.ExcludedShiftTypes.Clear();
        snapshot.ExcludedLocations.Clear();
        Assert.Single(store.GetSnapshot().ExcludedShiftTypes);
        Assert.Single(store.GetSnapshot().ExcludedLocations);
    }

    [Fact]
    public void LegacyConfig_DoesNotInterpretEmployeeNamesAsShiftTypes()
    {
        var config = JsonSerializer.Deserialize<AppConfig>(
            """{"ShiftRules":{"FavoriteShiftUsers":["Old employee"],"StartTimesToSkip":["22:00"]}}""")!;
        Assert.Empty(config.ShiftRules.PreferredShiftTypes);
        Assert.Empty(config.ShiftRules.ExcludedShiftTypes);
        Assert.Empty(config.ShiftRules.PreferredLocations);
        Assert.Empty(config.ShiftRules.ExcludedLocations);
        Assert.Equal(new[] { "22:00" }, config.ShiftRules.StartTimesToSkip);
    }
}
