using System;
using System.Collections.Generic;
using System.Reflection;
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
}
