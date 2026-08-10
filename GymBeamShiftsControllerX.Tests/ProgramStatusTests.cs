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
    public void CreateStatusSnapshot_FormatsSuccessAndErrorState()
    {
        ResetProgramState();
        DateTime iteration = DateTime.Now.AddMinutes(-1);
        DateTime success = DateTime.Now.AddMinutes(-2);
        DateTime error = DateTime.Now.AddMinutes(-3);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastIterationAt", iteration);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastSuccessAt", success);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastErrorAt", error);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastErrorMessage", "test error");

        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "CreateStatusSnapshot");
        var cfg = new AppConfig { Timing = new TimingSettings { CheckIntervalMinutes = 2 } };
        var snapshot = (BotStatusSnapshot)method.Invoke(null, new object[] { DateTime.Now.AddHours(-1), cfg })!;

        Assert.Equal(iteration.ToString("yyyy-MM-dd HH:mm:ss"), snapshot.LastIterationAt);
        Assert.Equal(success.ToString("yyyy-MM-dd HH:mm:ss"), snapshot.LastSuccessAt);
        Assert.Equal(error.ToString("yyyy-MM-dd HH:mm:ss"), snapshot.LastErrorAt);
        Assert.Equal("test error", snapshot.LastErrorMessage);
    }

    [Fact]
    public void CreateStatusSnapshot_UsesEmptyStringsBeforeAnyIterations()
    {
        ResetProgramState();
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "CreateStatusSnapshot");
        var cfg = new AppConfig { Timing = new TimingSettings { CheckIntervalMinutes = 2 } };

        var snapshot = (BotStatusSnapshot)method.Invoke(null, new object[] { DateTime.Now, cfg })!;

        Assert.False(snapshot.IsRunning);
        Assert.Equal(string.Empty, snapshot.LastIterationAt);
        Assert.Equal(string.Empty, snapshot.LastSuccessAt);
        Assert.Equal(string.Empty, snapshot.LastErrorAt);
        Assert.Equal(string.Empty, snapshot.LastErrorMessage);
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

    [Fact]
    public void TrySendTelegram_ReturnsFalse_WhenUrlCannotBeCreated()
    {
        ResetProgramState();
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "TrySendTelegram");
        var cfg = new AppConfig
        {
            Telegram = new TelegramSettings { BotToken = new string('x', 70000), ChatId = "chat" }
        };

        var result = (bool)method.Invoke(null, new object[] { cfg, "hello" })!;

        Assert.False(result);
    }

    [Fact]
    public void TrySendDailyStatus_DoesNothingWhenAlreadySentToday()
    {
        ResetProgramState();
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastDailyStatusDate", DateTime.Today);
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "TrySendDailyStatus");

        method.Invoke(null, new object[] { new AppConfig(), DateTime.Now.AddHours(-1) });

        Assert.Equal(DateTime.Today, ReflectionTestHelper.GetStaticField(typeof(Program), "lastDailyStatusDate"));
    }

    [Fact]
    public void TrySendErrorNotification_DoesNothingInsideThrottleWindow()
    {
        ResetProgramState();
        ReflectionTestHelper.SetStaticField(typeof(Program), "totalIterationCount", 10L);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastErrorNotificationIteration", 0L);
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "TrySendErrorNotification");

        method.Invoke(null, new object[] { new AppConfig(), new Exception("ignored"), "test" });

        Assert.Equal(0L, ReflectionTestHelper.GetStaticField(typeof(Program), "lastErrorNotificationIteration"));
    }

    [Fact]
    public void SendStartupNotification_HandlesTelegramFailureWithoutThrowing()
    {
        ResetProgramState();
        string? previousHost = Environment.GetEnvironmentVariable("HOSTNAME");
        string? previousContainer = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
        string? previousPort = Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_PORT");
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "SendStartupNotification");
        var cfg = CreateConfigWithInvalidTelegramUrl();

        try
        {
            Environment.SetEnvironmentVariable("HOSTNAME", "unit-test-host");
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", "true");
            Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_PORT", "9123");

            var exception = Record.Exception(() =>
                method.Invoke(null, new object[] { cfg, new DateTime(2026, 8, 10, 8, 30, 0) }));

            Assert.Null(exception);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOSTNAME", previousHost);
            Environment.SetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER", previousContainer);
            Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_PORT", previousPort);
        }
    }

    [Fact]
    public void TrySendErrorNotification_KeepsThrottleMarkerWhenTelegramFails()
    {
        ResetProgramState();
        ReflectionTestHelper.SetStaticField(typeof(Program), "totalIterationCount", 30L);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastErrorNotificationIteration", 0L);
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "TrySendErrorNotification");

        method.Invoke(null, new object[]
        {
            CreateConfigWithInvalidTelegramUrl(),
            new Exception("expected failure"),
            "unit test"
        });

        Assert.Equal(0L, ReflectionTestHelper.GetStaticField(typeof(Program), "lastErrorNotificationIteration"));
    }

    private static AppConfig CreateConfigWithInvalidTelegramUrl()
    {
        return new AppConfig
        {
            Telegram = new TelegramSettings
            {
                BotToken = new string('x', 70000),
                ChatId = "chat"
            }
        };
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
