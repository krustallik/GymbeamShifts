using System.Threading.Channels;
using GymBeamShiftsControllerX.Models;
using GymBeamShiftsControllerX.Services;
using Microsoft.Data.Sqlite;
using Moq;
using OpenQA.Selenium;

namespace GymBeamShiftsControllerX.Tests;

public sealed class ShiftCheckerBackgroundWorkTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TelegramSettings Original = new() { BotToken = "original-token", ChatId = "original-chat" };
    private readonly string _directory = Directory.CreateTempSubdirectory("gymbeam-telegram-").FullName;
    private string DatabasePath => Path.Combine(_directory, "telegram.sqlite3");

    [Fact]
    public void GetCookieBannerWaitTimeout_UsesLongWaitOncePerDriverSession()
    {
        var session = new BrowserSession(new AppConfig());
        var firstDriver = new Mock<IWebDriver>().Object;
        var restartedDriver = new Mock<IWebDriver>().Object;

        Assert.Equal(TimeSpan.FromSeconds(60), session.GetCookieBannerWaitTimeout(firstDriver));
        Assert.Equal(TimeSpan.FromSeconds(5), session.GetCookieBannerWaitTimeout(firstDriver));
        Assert.Equal(TimeSpan.FromSeconds(60), session.GetCookieBannerWaitTimeout(restartedDriver));
        Assert.Equal(TimeSpan.FromSeconds(5), session.GetCookieBannerWaitTimeout(restartedDriver));
    }

    [Fact]
    public async Task Queue_DoesNotBlockWhileSendingAndDeliversFreshConfirmationBeforeRepeat()
    {
        var sent = Channel.CreateUnbounded<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new TelegramNotificationQueue(Original, DatabasePath, async (_, _, message, stop) =>
        {
            sent.Writer.TryWrite(message);
            if (message == "important")
                await release.Task.WaitAsync(stop);
        });
        var timing = new TimingSettings
        {
            ImportantShiftNotificationCount = 2,
            ImportantShiftNotificationDelayMilliseconds = 1000,
            TelegramDelayMilliseconds = 0
        };

        queue.EnqueueConfirmation("important", true, timing, queue.RecipientVersion);
        Assert.Equal("important", await ReadAsync(sent));

        await Task.Run(() => queue.EnqueueConfirmation("fresh", false, timing, queue.RecipientVersion))
            .WaitAsync(TestTimeout);
        release.TrySetResult();
        Assert.Equal("fresh", await ReadAsync(sent));
        Assert.Equal("important", await ReadAsync(sent));
        Assert.True(await queue.Enqueue("barrier").WaitAsync(TestTimeout));
        Assert.Equal("barrier", await ReadAsync(sent));
        Assert.False(sent.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Queue_ContinuesAfterFailureAndKeepsDurableConfirmationForRetry()
    {
        var sent = Channel.CreateUnbounded<string>();
        await using var queue = new TelegramNotificationQueue(Original, DatabasePath, (_, _, message, _) =>
        {
            sent.Writer.TryWrite(message);
            return message == "failure" ? Task.FromException(new HttpRequestException("temporary")) : Task.CompletedTask;
        });

        queue.EnqueueConfirmation("failure", false, new TimingSettings(), queue.RecipientVersion);
        Assert.Equal("failure", await ReadAsync(sent));
        Assert.True(await queue.Enqueue("target without button").WaitAsync(TestTimeout));
        Assert.Equal("target without button", await ReadAsync(sent));

        using var store = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        store.Open();
        using var command = store.CreateCommand();
        command.CommandText = "SELECT message FROM confirmations";
        Assert.Equal("failure", command.ExecuteScalar());
    }

    [Fact]
    public async Task Queue_CancelsOldWorkWhenRecipientChangesAndRejectsLateEventsFromOldBrowser()
    {
        var sent = Channel.CreateUnbounded<(string Token, string Chat, string Message)>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var queue = new TelegramNotificationQueue(Original, DatabasePath, async (token, chat, message, stop) =>
        {
            sent.Writer.TryWrite((token, chat, message));
            if (message == "in progress")
                await release.Task.WaitAsync(stop);
        });
        var timing = new TimingSettings { ImportantShiftNotificationCount = 4, ImportantShiftNotificationDelayMilliseconds = 0 };
        long oldVersion = queue.RecipientVersion;
        queue.EnqueueConfirmation("in progress", true, timing, oldVersion);
        Assert.Equal(("original-token", "original-chat", "in progress"), await ReadAsync(sent));

        queue.EnqueueConfirmation("queued for old chat", false, timing, oldVersion);
        Task<bool> oldAlert = queue.Enqueue("old alert", oldVersion);
        queue.UpdateRecipient(new TelegramSettings { BotToken = "new-token", ChatId = "new-chat" });
        queue.EnqueueConfirmation("late event", false, timing, oldVersion);
        Assert.False(await queue.Enqueue("late alert", oldVersion));
        Assert.False(await oldAlert.WaitAsync(TestTimeout));
        queue.EnqueueConfirmation("new confirmation", false, timing, queue.RecipientVersion);
        release.TrySetResult();

        Assert.Equal(("new-token", "new-chat", "new confirmation"), await ReadAsync(sent));
        queue.UpdateRecipient(Original);
        Assert.True(await queue.Enqueue("barrier").WaitAsync(TestTimeout));
        Assert.Equal(("original-token", "original-chat", "barrier"), await ReadAsync(sent));
        Assert.False(sent.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Queue_AfterRestartRestoresOnlyConfirmationsForMatchingRecipient(bool sameRecipient)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var original = new TelegramNotificationQueue(Original, DatabasePath, async (_, _, _, stop) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, stop);
        }))
        {
            original.EnqueueConfirmation("recover me", false, new TimingSettings { TelegramDelayMilliseconds = 0 }, original.RecipientVersion);
            await started.Task.WaitAsync(TestTimeout);
        }

        var settings = sameRecipient ? Original : new TelegramSettings { BotToken = "new-token", ChatId = "new-chat" };
        var sent = Channel.CreateUnbounded<string>();
        await using var restarted = new TelegramNotificationQueue(settings, DatabasePath, (_, _, message, _) =>
        {
            sent.Writer.TryWrite(message);
            return Task.CompletedTask;
        });

        Assert.True(await restarted.Enqueue("barrier").WaitAsync(TestTimeout));
        if (sameRecipient)
            Assert.Equal("recover me", await ReadAsync(sent));

        Assert.Equal("barrier", await ReadAsync(sent));
        Assert.False(sent.Reader.TryRead(out _));
    }

    private static Task<T> ReadAsync<T>(Channel<T> channel) => channel.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
