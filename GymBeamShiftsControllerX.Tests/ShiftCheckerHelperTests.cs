using System;
using System.Collections.Generic;
using System.Reflection;
using GymBeamShiftsControllerX.Models;
using GymBeamShiftsControllerX.Services;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

public class ShiftCheckerHelperTests
{
    [Fact]
    public void ParseDateSet_ParsesValidDates_AndSkipsInvalid()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(ShiftChecker), "ParseDateSet");

        var result = (HashSet<DateTime>)method.Invoke(null, new object[]
        {
            new List<string> { "2026-05-09", "invalid", "2026-01-01" }
        })!;

        Assert.Contains(new DateTime(2026, 5, 9), result);
        Assert.Contains(new DateTime(2026, 1, 1), result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseWeekdaySet_ParsesCaseInsensitiveValues()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(ShiftChecker), "ParseWeekdaySet");

        var result = (HashSet<DayOfWeek>)method.Invoke(null, new object[]
        {
            new List<string> { "monday", "FRIDAY", "oops" }
        })!;

        Assert.Contains(DayOfWeek.Monday, result);
        Assert.Contains(DayOfWeek.Friday, result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void ParseFavoriteShiftUserPriorities_TrimsSkipsEmptyAndDedupesCaseInsensitive()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(ShiftChecker), "ParseFavoriteShiftUserPriorities");

        var result = (Dictionary<string, int>)method.Invoke(null, new object[]
        {
            new List<string> { " Andrea Pavlíková ", "", "andrea pavlíková", "Lukáš Fialek" }
        })!;

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result["Andrea Pavlíková"]);
        Assert.Equal(1, result["Lukáš Fialek"]);
    }

    [Fact]
    public void GetFavoriteShiftUserPriority_UnknownOrEmptyUser_ReturnsMaxValue()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(ShiftChecker), "GetFavoriteShiftUserPriority");
        var priorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Known User"] = 0
        };

        var emptyUserShift = new ShiftEntry { UserId = "  " };
        var unknownUserShift = new ShiftEntry { UserId = "Other" };

        var emptyPriority = (int)method.Invoke(null, new object[] { emptyUserShift, priorities })!;
        var unknownPriority = (int)method.Invoke(null, new object[] { unknownUserShift, priorities })!;

        Assert.Equal(int.MaxValue, emptyPriority);
        Assert.Equal(int.MaxValue, unknownPriority);
    }

    [Fact]
    public void PrioritizeShiftsByFavoriteUsers_EmptyFavorites_KeepsOriginalOrder()
    {
        var shifts = CreateSampleShifts();
        var result = Prioritize(Array.Empty<string>(), shifts);

        Assert.Equal(shifts.Select(s => s.UserId).ToArray(), result.Select(s => s.UserId).ToArray());
    }

    [Fact]
    public void PrioritizeShiftsByFavoriteUsers_SingleUserSameDate_KeepsOriginalOrder()
    {
        var shifts = new List<ShiftEntry>
        {
            new() { Date = new DateTime(2026, 6, 19), UserId = "Same User", TimeFrom = "18:00" },
            new() { Date = new DateTime(2026, 6, 19), UserId = "Same User", TimeFrom = "20:00" }
        };

        var result = Prioritize(new[] { "Same User" }, shifts);

        Assert.Equal(new[] { "18:00", "20:00" }, result.Select(s => s.TimeFrom).ToArray());
    }

    [Fact]
    public void PrioritizeShiftsByFavoriteUsers_OrdersFavoriteUserFirstWithinSameDate()
    {
        var shifts = new List<ShiftEntry>
        {
            new() { Date = new DateTime(2026, 6, 19), UserId = "Other User" },
            new() { Date = new DateTime(2026, 6, 19), UserId = "Andrea Pavlíková" },
            new() { Date = new DateTime(2026, 6, 20), UserId = "First Non Favorite" },
            new() { Date = new DateTime(2026, 6, 20), UserId = "Second Non Favorite" }
        };

        var result = Prioritize(new[] { "Andrea Pavlíková" }, shifts);

        Assert.Equal("Andrea Pavlíková", result[0].UserId);
        Assert.Equal("Other User", result[1].UserId);
        Assert.Equal("First Non Favorite", result[2].UserId);
        Assert.Equal("Second Non Favorite", result[3].UserId);
    }

    [Fact]
    public void PrioritizeShiftsByFavoriteUsers_NoFavoriteOnDate_KeepsOriginalOrder()
    {
        var shifts = new List<ShiftEntry>
        {
            new() { Date = new DateTime(2026, 6, 19), UserId = "User A" },
            new() { Date = new DateTime(2026, 6, 19), UserId = "User B" }
        };

        var result = Prioritize(new[] { "Missing Favorite" }, shifts);

        Assert.Equal(new[] { "User A", "User B" }, result.Select(s => s.UserId).ToArray());
    }

    [Fact]
    public void PrioritizeShiftsByFavoriteUsers_UsesConfigOrderForMultipleFavorites()
    {
        var shifts = new List<ShiftEntry>
        {
            new() { Date = new DateTime(2026, 6, 19), UserId = "Lukáš Fialek" },
            new() { Date = new DateTime(2026, 6, 19), UserId = "Andrea Pavlíková" },
            new() { Date = new DateTime(2026, 6, 19), UserId = "Other User" }
        };

        var result = Prioritize(new[] { "Andrea Pavlíková", "Lukáš Fialek" }, shifts);

        Assert.Equal(new[] { "Andrea Pavlíková", "Lukáš Fialek", "Other User" }, result.Select(s => s.UserId).ToArray());
    }

    [Fact]
    public void PrioritizeShiftsByFavoriteUsers_MatchesFavoriteCaseInsensitively()
    {
        var shifts = new List<ShiftEntry>
        {
            new() { Date = new DateTime(2026, 6, 19), UserId = "Other User" },
            new() { Date = new DateTime(2026, 6, 19), UserId = "andrea pavlíková" }
        };

        var result = Prioritize(new[] { "Andrea Pavlíková" }, shifts);

        Assert.Equal("andrea pavlíková", result[0].UserId);
        Assert.Equal("Other User", result[1].UserId);
    }

    [Fact]
    public void PrioritizeShiftsByFavoriteUsers_PreservesOriginalIndexWhenPriorityEqual()
    {
        var shifts = new List<ShiftEntry>
        {
            new() { Date = new DateTime(2026, 6, 19), UserId = "User B" },
            new() { Date = new DateTime(2026, 6, 19), UserId = "User A" }
        };

        var result = Prioritize(Array.Empty<string>(), shifts);

        Assert.Equal(new[] { "User B", "User A" }, result.Select(s => s.UserId).ToArray());
    }

    private static List<ShiftEntry> CreateSampleShifts()
    {
        return new List<ShiftEntry>
        {
            new() { Date = new DateTime(2026, 6, 19), UserId = "A" },
            new() { Date = new DateTime(2026, 6, 20), UserId = "B" }
        };
    }

    private static List<ShiftEntry> Prioritize(IEnumerable<string> favorites, List<ShiftEntry> shifts)
    {
        var parseMethod = ReflectionTestHelper.GetStaticMethod(typeof(ShiftChecker), "ParseFavoriteShiftUserPriorities");
        var prioritizeMethod = ReflectionTestHelper.GetStaticMethod(typeof(ShiftChecker), "PrioritizeShiftsByFavoriteUsers");

        var priorities = (Dictionary<string, int>)parseMethod.Invoke(null, new object[] { favorites.ToList() })!;
        return (List<ShiftEntry>)prioritizeMethod.Invoke(null, new object[] { shifts, priorities })!;
    }
}
