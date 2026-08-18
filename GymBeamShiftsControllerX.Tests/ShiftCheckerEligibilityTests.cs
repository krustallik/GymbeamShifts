using System;
using System.Collections.Generic;
using System.Reflection;
using GymBeamShiftsControllerX.Models;
using GymBeamShiftsControllerX.Services;
using OpenQA.Selenium;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

public class ShiftCheckerEligibilityTests
{
    private static readonly MethodInfo IsRelevantShiftMethod =
        ReflectionTestHelper.GetStaticMethod(typeof(ShiftChecker), "IsRelevantShift");

    private static readonly MethodInfo TryParseShiftStartMethod =
        ReflectionTestHelper.GetStaticMethod(typeof(ShiftChecker), "TryParseShiftStart");

    [Fact]
    public void TryParseShiftStart_ValidTime_ReturnsShiftStart()
    {
        var shift = CreateShift(new DateTime(2026, 6, 19), "18:30");

        var args = new object?[] { shift, null };
        var parsed = (bool)TryParseShiftStartMethod.Invoke(null, args)!;

        Assert.True(parsed);
        Assert.Equal(new DateTime(2026, 6, 19, 18, 30, 0), args[1]);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("18")]
    [InlineData("aa:bb")]
    [InlineData("18:aa")]
    public void TryParseShiftStart_InvalidTime_ReturnsFalse(string timeFrom)
    {
        var shift = CreateShift(new DateTime(2026, 6, 19), timeFrom);
        var args = new object?[] { shift, null };

        var parsed = (bool)TryParseShiftStartMethod.Invoke(null, args)!;

        Assert.False(parsed);
    }

    [Fact]
    public void IsRelevantShift_SkipsStartTimesToSkip()
    {
        var shift = CreateEligibleShift(new DateTime(2026, 6, 19), "18:00");
        Assert.False(InvokeIsRelevant(shift, startTimesToSkip: new List<string> { "18:00" }));
    }

    [Fact]
    public void IsRelevantShift_WeekendIgnoresStartTimesToSkip()
    {
        var shift = CreateEligibleShift(new DateTime(2026, 6, 20), "18:00");

        Assert.True(InvokeIsRelevant(
            shift,
            startTimesToSkip: new List<string> { "18:00" },
            includedWeekdays: new HashSet<DayOfWeek>()));
    }

    [Fact]
    public void IsRelevantShift_HolidayIgnoresStartTimesToSkip()
    {
        var holiday = new DateTime(2026, 6, 17);
        var shift = CreateEligibleShift(holiday, "18:00");

        Assert.True(InvokeIsRelevant(
            shift,
            holidays: new HashSet<DateTime> { holiday },
            startTimesToSkip: new List<string> { "18:00" },
            includedWeekdays: new HashSet<DayOfWeek>()));
    }

    [Fact]
    public void IsRelevantShift_SkipsExcludedDates()
    {
        var date = new DateTime(2026, 6, 19);
        var shift = CreateEligibleShift(date, "18:00");
        Assert.False(InvokeIsRelevant(shift, excludedDates: new HashSet<DateTime> { date.Date }));
    }

    [Fact]
    public void IsRelevantShift_TakesHolidayEvenOnWeekday()
    {
        var date = new DateTime(2026, 6, 17); // Wednesday
        var shift = CreateEligibleShift(date, "18:00");
        Assert.True(InvokeIsRelevant(
            shift,
            holidays: new HashSet<DateTime> { date.Date },
            includedWeekdays: new HashSet<DayOfWeek>()));
    }

    [Fact]
    public void IsRelevantShift_TakesIncludedWeekday()
    {
        var shift = CreateEligibleShift(new DateTime(2026, 6, 19), "18:00");
        Assert.True(InvokeIsRelevant(
            shift,
            includedWeekdays: new HashSet<DayOfWeek> { DayOfWeek.Friday }));
    }

    [Fact]
    public void IsRelevantShift_TakesWeekend()
    {
        var shift = CreateEligibleShift(new DateTime(2026, 6, 20), "10:00");
        Assert.True(InvokeIsRelevant(
            shift,
            includedWeekdays: new HashSet<DayOfWeek>()));
    }

    [Theory]
    [InlineData(27, 59, false)]
    [InlineData(28, 0, true)]
    [InlineData(47, 59, true)]
    public void IsRelevantShift_Uses28HourMinimumForWeekend(int hoursAhead, int minutesAhead, bool expected)
    {
        var now = new DateTime(2026, 6, 19, 10, 0, 0); // Friday
        var shiftStart = now.AddHours(hoursAhead).AddMinutes(minutesAhead);
        var shift = CreateEligibleShift(shiftStart.Date, shiftStart.ToString("HH:mm"));

        Assert.Equal(expected, InvokeIsRelevant(
            shift,
            includedWeekdays: new HashSet<DayOfWeek>(),
            shiftMinHoursAhead: 48,
            now: now));
    }

    [Fact]
    public void IsRelevantShift_Uses28HourMinimumForHoliday()
    {
        var now = new DateTime(2026, 6, 16, 12, 0, 0);
        var holiday = new DateTime(2026, 6, 17);
        var shift = CreateEligibleShift(holiday, "18:00"); // 30 hours ahead

        Assert.True(InvokeIsRelevant(
            shift,
            holidays: new HashSet<DateTime> { holiday },
            includedWeekdays: new HashSet<DayOfWeek>(),
            shiftMinHoursAhead: 48,
            now: now));
    }

    [Fact]
    public void IsRelevantShift_UsesConfiguredWeekendOrHolidayMinimum()
    {
        var now = new DateTime(2026, 6, 19, 10, 0, 0);
        var shift = CreateEligibleShift(new DateTime(2026, 6, 20), "10:00"); // 24 hours ahead

        Assert.True(InvokeIsRelevant(
            shift,
            includedWeekdays: new HashSet<DayOfWeek>(),
            shiftMinHoursAhead: 48,
            weekendOrHolidayMinHoursAhead: 20,
            now: now));
    }

    [Fact]
    public void IsRelevantShift_KeepsConfiguredMinimumForIncludedWeekday()
    {
        var now = new DateTime(2026, 6, 18, 12, 0, 0); // Thursday
        var shift = CreateEligibleShift(new DateTime(2026, 6, 19), "18:00"); // 30 hours ahead

        Assert.False(InvokeIsRelevant(
            shift,
            includedWeekdays: new HashSet<DayOfWeek> { DayOfWeek.Friday },
            shiftMinHoursAhead: 48,
            now: now));
    }

    [Fact]
    public void IsRelevantShift_SkipsPlainWeekdayNotIncluded()
    {
        var shift = CreateEligibleShift(new DateTime(2026, 6, 17), "18:00");
        Assert.False(InvokeIsRelevant(
            shift,
            includedWeekdays: new HashSet<DayOfWeek> { DayOfWeek.Friday }));
    }

    [Fact]
    public void IsRelevantShift_SkipsWhenShiftStartsTooSoon()
    {
        var now = new DateTime(2026, 6, 17, 12, 0, 0);
        var shift = CreateEligibleShift(new DateTime(2026, 6, 18), "10:00");
        Assert.False(InvokeIsRelevant(shift, shiftMinHoursAhead: 48, now: now));
    }

    [Fact]
    public void IsRelevantShift_SkipsWhenButtonMissing()
    {
        var shift = CreateEligibleShift(new DateTime(2026, 6, 19), "18:00");
        shift.ButtonElement = null!;
        Assert.False(InvokeIsRelevant(shift));
    }

    [Fact]
    public void IsRelevantShift_SkipsWhenMinuteCannotBeParsed()
    {
        var shift = CreateEligibleShift(new DateTime(2026, 6, 19), "18:invalid");

        Assert.False(InvokeIsRelevant(shift));
    }

    [Fact]
    public void IsRelevantShift_FavoritePriorityDoesNotFilterNonFavorite()
    {
        var favorite = CreateEligibleShift(new DateTime(2026, 6, 19), "18:00", "Favorite User");
        var other = CreateEligibleShift(new DateTime(2026, 6, 19), "20:00", "Other User");

        Assert.True(InvokeIsRelevant(favorite));
        Assert.True(InvokeIsRelevant(other));
    }

    private static bool InvokeIsRelevant(
        ShiftEntry shift,
        HashSet<DateTime>? holidays = null,
        HashSet<DateTime>? excludedDates = null,
        List<string>? startTimesToSkip = null,
        HashSet<DayOfWeek>? includedWeekdays = null,
        int shiftMinHoursAhead = 1,
        int weekendOrHolidayMinHoursAhead = 28,
        DateTime? now = null)
    {
        return (bool)IsRelevantShiftMethod.Invoke(null, new object?[]
        {
            shift,
            holidays ?? new HashSet<DateTime>(),
            excludedDates ?? new HashSet<DateTime>(),
            startTimesToSkip ?? new List<string>(),
            includedWeekdays ?? new HashSet<DayOfWeek> { DayOfWeek.Friday },
            shiftMinHoursAhead,
            weekendOrHolidayMinHoursAhead,
            now ?? new DateTime(2026, 6, 1, 0, 0, 0)
        })!;
    }

    private static ShiftEntry CreateEligibleShift(DateTime date, string timeFrom, string userId = "User")
    {
        return CreateShift(date, timeFrom, userId, TestWebElements.CreateButton());
    }

    private static ShiftEntry CreateShift(DateTime date, string timeFrom, string userId = "User", IWebElement? button = null)
    {
        return new ShiftEntry
        {
            Date = date,
            TimeFrom = timeFrom,
            TimeTo = "22:00",
            UserId = userId,
            ButtonElement = button!
        };
    }
}
