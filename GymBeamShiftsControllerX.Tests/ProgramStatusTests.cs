using System;
using System.Reflection;
using GymBeamShiftsControllerX;
using GymBeamShiftsControllerX.Models;
using GymBeamShiftsControllerX.Services;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

public class ProgramStatusTests
{
    [Fact]
    public void FormatUptime_ReturnsExpectedFormat()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "FormatUptime");
        var result = (string)method.Invoke(null, new object[] { new TimeSpan(2, 5, 10, 0) })!;
        Assert.Equal("2d 5h 10m", result);
    }

    [Fact]
    public void CreateStatusSnapshot_ReturnsRunningTrue_WhenLastIterationRecent()
    {
        ResetProgramState();
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastIterationAt", DateTime.Now.AddMinutes(-1));
        ReflectionTestHelper.SetStaticField(typeof(Program), "totalIterationCount", 10L);

        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "CreateStatusSnapshot");
        var cfg = new AppConfig { Timing = new TimingSettings { CheckIntervalMinutes = 2 } };
        var snapshot = (BotStatusSnapshot)method.Invoke(null, new object[] { DateTime.Now.AddHours(-1), cfg })!;

        Assert.True(snapshot.IsRunning);
        Assert.Equal(10, snapshot.TotalIterations);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.LastIterationAt));
    }

    [Fact]
    public void CreateStatusSnapshot_ReturnsRunningFalse_WhenLagTooLarge()
    {
        ResetProgramState();
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastIterationAt", DateTime.Now.AddMinutes(-30));

        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "CreateStatusSnapshot");
        var cfg = new AppConfig { Timing = new TimingSettings { CheckIntervalMinutes = 2 } };
        var snapshot = (BotStatusSnapshot)method.Invoke(null, new object[] { DateTime.Now.AddHours(-1), cfg })!;

        Assert.False(snapshot.IsRunning);
    }

    [Fact]
    public void ShouldSendDailyStatus_ReturnsFalse_BeforeDailyTime()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "ShouldSendDailyStatus");
        var now = DateTime.Today.AddHours(9);
        var result = (bool)method.Invoke(null, new object[] { now, DateTime.MinValue.Date })!;
        Assert.False(result);
    }

    [Fact]
    public void ShouldSendDailyStatus_ReturnsTrue_AfterDailyTimeWhenNotSentToday()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "ShouldSendDailyStatus");
        var now = DateTime.Today.AddHours(11);
        var result = (bool)method.Invoke(null, new object[] { now, DateTime.MinValue.Date })!;
        Assert.True(result);
    }

    [Fact]
    public void ShouldSendDailyStatus_ReturnsFalse_WhenAlreadySentToday()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "ShouldSendDailyStatus");
        var now = DateTime.Today.AddHours(11);
        var result = (bool)method.Invoke(null, new object[] { now, now.Date })!;
        Assert.False(result);
    }

    [Theory]
    [InlineData(30, 0, true)]
    [InlineData(29, 0, false)]
    [InlineData(60, 30, true)]
    public void ShouldSendErrorNotification_RespectsIterationThrottle(long total, long lastSent, bool expected)
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "ShouldSendErrorNotification");
        var result = (bool)method.Invoke(null, new object[] { total, lastSent })!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void TrySendTelegram_CompletesWithoutThrowing_ForInvalidToken()
    {
        ResetProgramState();
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "TrySendTelegram");
        var cfg = new AppConfig
        {
            Telegram = new TelegramSettings { BotToken = string.Empty, ChatId = string.Empty }
        };

        var exception = Record.Exception(() => method.Invoke(null, new object[] { cfg, "hello" }));
        Assert.Null(exception);
    }

    private static void ResetProgramState()
    {
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastIterationAt", DateTime.MinValue);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastSuccessAt", DateTime.MinValue);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastErrorAt", DateTime.MinValue);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastErrorMessage", string.Empty);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastDailyStatusDate", DateTime.MinValue.Date);
        ReflectionTestHelper.SetStaticField(typeof(Program), "totalIterationCount", 0L);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastErrorNotificationIteration", -30L);
    }
}
