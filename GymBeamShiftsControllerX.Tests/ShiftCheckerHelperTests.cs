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
        var method = typeof(ShiftChecker).GetMethod("ParseDateSet", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ParseDateSet not found.");

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
        var method = typeof(ShiftChecker).GetMethod("ParseWeekdaySet", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ParseWeekdaySet not found.");

        var result = (HashSet<DayOfWeek>)method.Invoke(null, new object[]
        {
            new List<string> { "monday", "FRIDAY", "oops" }
        })!;

        Assert.Contains(DayOfWeek.Monday, result);
        Assert.Contains(DayOfWeek.Friday, result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void PrioritizeShiftsByFavoriteUsers_OrdersFavoriteUserFirstWithinSameDate()
    {
        var parseMethod = typeof(ShiftChecker).GetMethod("ParseFavoriteShiftUserPriorities", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ParseFavoriteShiftUserPriorities not found.");
        var prioritizeMethod = typeof(ShiftChecker).GetMethod("PrioritizeShiftsByFavoriteUsers", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("PrioritizeShiftsByFavoriteUsers not found.");

        var priorities = (Dictionary<string, int>)parseMethod.Invoke(null, new object[]
        {
            new List<string> { "Andrea Pavlíková" }
        })!;

        var shifts = new List<ShiftEntry>
        {
            new ShiftEntry { Date = new DateTime(2026, 6, 19), UserId = "Other User" },
            new ShiftEntry { Date = new DateTime(2026, 6, 19), UserId = "Andrea Pavlíková" },
            new ShiftEntry { Date = new DateTime(2026, 6, 20), UserId = "First Non Favorite" },
            new ShiftEntry { Date = new DateTime(2026, 6, 20), UserId = "Second Non Favorite" }
        };

        var result = (List<ShiftEntry>)prioritizeMethod.Invoke(null, new object[]
        {
            shifts,
            priorities
        })!;

        Assert.Equal("Andrea Pavlíková", result[0].UserId);
        Assert.Equal("Other User", result[1].UserId);
        Assert.Equal("First Non Favorite", result[2].UserId);
        Assert.Equal("Second Non Favorite", result[3].UserId);
    }
}
