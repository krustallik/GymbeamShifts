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
    public void ParseDateSet_Null_ReturnsEmptySet()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(ShiftChecker), "ParseDateSet");

        var result = (HashSet<DateTime>)method.Invoke(null, new object?[] { null })!;

        Assert.Empty(result);
    }

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
    public void ParseWeekdaySet_Null_ReturnsEmptySet()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(ShiftChecker), "ParseWeekdaySet");

        var result = (HashSet<DayOfWeek>)method.Invoke(null, new object?[] { null })!;

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("2026-06-20", false, true)]
    [InlineData("2026-06-21", false, true)]
    [InlineData("2026-06-17", true, true)]
    [InlineData("2026-06-17", false, false)]
    public void IsWeekendOrHoliday_ReturnsExpectedValue(
        string date,
        bool includeAsHoliday,
        bool expected)
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(ShiftChecker), "IsWeekendOrHoliday");
        DateTime parsedDate = DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture);
        var holidays = includeAsHoliday
            ? new HashSet<DateTime> { parsedDate }
            : new HashSet<DateTime>();

        var result = (bool)method.Invoke(null, new object[]
        {
            new ShiftEntry { Date = parsedDate },
            holidays
        })!;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void NotifyAboutUnavailableTargetShifts_SendsMessageForEachExactMissingButton()
    {
        var messages = new List<string>();
        var config = new AppConfig();
        var checker = new ShiftChecker(
            new BrowserSession(config),
            config,
            new ShiftRulesStore(config.ShiftRules),
            messages.Add);
        var method = ReflectionTestHelper.GetInstanceMethod(
            typeof(ShiftChecker),
            "NotifyAboutUnavailableTargetShifts");
        var shifts = new List<ShiftEntry>
        {
            new()
            {
                Date = new DateTime(2026, 8, 25),
                TimeFrom = "08:00",
                TimeTo = "16:00",
                ShiftType = "Target User",
                ButtonElement = null
            },
            new()
            {
                Date = new DateTime(2026, 8, 26),
                TimeFrom = "09:30",
                TimeTo = "17:30",
                ShiftType = "Second Target",
                ButtonElement = null
            }
        };

        int result = (int)method.Invoke(
            checker,
            new object[]
            {
                shifts,
                new List<string> { "2026-08-25T08:00", "2026-08-26T09:30" },
                new HashSet<DateTime>()
            })!;

        Assert.Equal(2, result);
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, message => message.Contains("Час: 08:00-16:00"));
        Assert.Contains(messages, message => message.Contains("Час: 09:30-17:30"));
        Assert.All(messages, message => Assert.Contains("немає кнопки «Prihlásiť»", message));
    }

    [Fact]
    public void NotifyAboutUnavailableTargetShifts_RequiresExactDateTimeAndMissingButton()
    {
        var messages = new List<string>();
        var config = new AppConfig();
        var checker = new ShiftChecker(
            new BrowserSession(config),
            config,
            new ShiftRulesStore(config.ShiftRules),
            messages.Add);
        var method = ReflectionTestHelper.GetInstanceMethod(
            typeof(ShiftChecker),
            "NotifyAboutUnavailableTargetShifts");
        var shifts = new List<ShiftEntry>
        {
            new()
            {
                Date = new DateTime(2026, 8, 25),
                TimeFrom = "08:00",
                TimeTo = "16:00",
                ShiftType = "Target User",
                ButtonElement = TestWebElements.CreateButton()
            }
        };

        int withButton = (int)method.Invoke(
            checker,
            new object[]
            {
                shifts,
                new List<string> { "2026-08-25T08:00" },
                new HashSet<DateTime>()
            })!;
        shifts[0].ButtonElement = null;
        int wrongTime = (int)method.Invoke(
            checker,
            new object[]
            {
                shifts,
                new List<string> { "2026-08-25T09:00" },
                new HashSet<DateTime>()
            })!;

        Assert.Equal(0, withButton);
        Assert.Equal(0, wrongTime);
        Assert.Empty(messages);
    }

    [Fact]
    public void BuildSuccessfulShiftMessage_IsUkrainian()
    {
        var method = ReflectionTestHelper.GetStaticMethod(
            typeof(ShiftChecker),
            "BuildSuccessfulShiftMessage");
        var shift = new ShiftEntry
        {
            Date = new DateTime(2026, 8, 25),
            TimeFrom = "08:00",
            TimeTo = "16:00",
            ShiftType = "Successful User"
        };

        string message = (string)method.Invoke(null, new object[] { shift })!;

        Assert.Contains("Зміну знайдено та успішно обрано", message);
        Assert.Contains("Дата: 25.08.2026", message);
        Assert.Contains("Тип зміни: Successful User", message);
        Assert.DoesNotContain("Shift found:", message);
    }

}
