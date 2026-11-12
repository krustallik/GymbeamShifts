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
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastIterationAt", UserTime.Now.AddMinutes(-1));
        ReflectionTestHelper.SetStaticField(typeof(Program), "totalIterationCount", 10L);

        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "CreateStatusSnapshot");
        var cfg = new AppConfig { Timing = new TimingSettings { CheckIntervalMinutes = 2 } };
        var snapshot = (BotStatusSnapshot)method.Invoke(null, new object[] { UserTime.Now.AddHours(-1), cfg })!;

        Assert.True(snapshot.IsRunning);
        Assert.Equal(10, snapshot.TotalIterations);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.LastIterationAt));
    }

    [Fact]
    public void CreateStatusSnapshot_ReturnsRunningFalse_WhenLagTooLarge()
    {
        ResetProgramState();
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastIterationAt", UserTime.Now.AddMinutes(-30));

        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "CreateStatusSnapshot");
        var cfg = new AppConfig { Timing = new TimingSettings { CheckIntervalMinutes = 2 } };
        var snapshot = (BotStatusSnapshot)method.Invoke(null, new object[] { UserTime.Now.AddHours(-1), cfg })!;

        Assert.False(snapshot.IsRunning);
    }

    [Fact]
    public void CreateStatusSnapshot_FormatsSuccessAndErrorState()
    {
        ResetProgramState();
        DateTime iteration = UserTime.Now.AddMinutes(-1);
        DateTime success = UserTime.Now.AddMinutes(-2);
        DateTime error = UserTime.Now.AddMinutes(-3);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastIterationAt", iteration);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastSuccessAt", success);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastErrorAt", error);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastErrorMessage", "test error");

        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "CreateStatusSnapshot");
        var cfg = new AppConfig { Timing = new TimingSettings { CheckIntervalMinutes = 2 } };
        var snapshot = (BotStatusSnapshot)method.Invoke(null, new object[] { UserTime.Now.AddHours(-1), cfg })!;

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

        var snapshot = (BotStatusSnapshot)method.Invoke(null, new object[] { UserTime.Now, cfg })!;

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
        var now = UserTime.Now.Date.AddHours(9);
        var result = (bool)method.Invoke(null, new object[] { now, DateTime.MinValue.Date })!;
        Assert.False(result);
    }

    [Fact]
    public void ShouldSendDailyStatus_ReturnsTrue_AfterDailyTimeWhenNotSentToday()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "ShouldSendDailyStatus");
        var now = UserTime.Now.Date.AddHours(11);
        var result = (bool)method.Invoke(null, new object[] { now, DateTime.MinValue.Date })!;
        Assert.True(result);
    }

    [Fact]
    public void ShouldSendDailyStatus_ReturnsFalse_WhenAlreadySentToday()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "ShouldSendDailyStatus");
        var now = UserTime.Now.Date.AddHours(11);
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
    public async Task TrySendDailyStatus_DoesNothingWhenAlreadySentToday()
    {
        ResetProgramState();
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastDailyStatusDate", UserTime.Now.Date);
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "TrySendDailyStatus");

        await (Task)method.Invoke(null, new object?[] { null, UserTime.Now.AddHours(-1) })!;

        Assert.Equal(UserTime.Now.Date, ReflectionTestHelper.GetStaticField(typeof(Program), "lastDailyStatusDate"));
    }

    [Fact]
    public async Task TrySendErrorNotification_DoesNothingInsideThrottleWindow()
    {
        ResetProgramState();
        ReflectionTestHelper.SetStaticField(typeof(Program), "totalIterationCount", 10L);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastErrorNotificationIteration", 0L);
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "TrySendErrorNotification");

        await (Task)method.Invoke(null, new object?[] { null })!;

        Assert.Equal(0L, ReflectionTestHelper.GetStaticField(typeof(Program), "lastErrorNotificationIteration"));
    }

    [Theory]
    [InlineData(false, 0L)]
    [InlineData(true, 30L)]
    public async Task TrySendErrorNotification_MarksOnlySuccessfulDelivery(bool succeeds, long expectedMarker)
    {
        ResetProgramState();
        ReflectionTestHelper.SetStaticField(typeof(Program), "totalIterationCount", 30L);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastErrorNotificationIteration", 0L);
        var method = ReflectionTestHelper.GetStaticMethod(typeof(Program), "TrySendErrorNotification");
        string path = Path.Combine(Path.GetTempPath(), $"gymbeam-program-{Guid.NewGuid():N}.sqlite3");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;

        try
        {
            await using var queue = new TelegramNotificationQueue(
                new TelegramSettings { BotToken = "test-token", ChatId = "test-chat" }, path,
                async (_, _, _, cancellation) =>
                {
                    Interlocked.Increment(ref attempts);
                    started.TrySetResult();
                    await release.Task.WaitAsync(cancellation);
                    if (!succeeds)
                        throw new HttpRequestException("test");
                });
            var delivery = (Task)method.Invoke(null, new object[] { queue })!;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0L, ReflectionTestHelper.GetStaticField(typeof(Program), "lastErrorNotificationIteration"));
            await (Task)method.Invoke(null, new object[] { queue })!;
            release.TrySetResult();
            await delivery.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(expectedMarker, ReflectionTestHelper.GetStaticField(typeof(Program), "lastErrorNotificationIteration"));
            Assert.Equal(1, attempts);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void ResetProgramState()
    {
        ReflectionTestHelper.SetStaticField(typeof(Program), "dailyStatusPending", false);
        ReflectionTestHelper.SetStaticField(typeof(Program), "errorNotificationPending", false);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastIterationAt", DateTime.MinValue);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastSuccessAt", DateTime.MinValue);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastErrorAt", DateTime.MinValue);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastErrorMessage", string.Empty);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastDailyStatusDate", DateTime.MinValue.Date);
        ReflectionTestHelper.SetStaticField(typeof(Program), "totalIterationCount", 0L);
        ReflectionTestHelper.SetStaticField(typeof(Program), "lastErrorNotificationIteration", -30L);
    }
}
