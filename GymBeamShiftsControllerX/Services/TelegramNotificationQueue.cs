using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using GymBeamShiftsControllerX.Models;
using Microsoft.Data.Sqlite;

namespace GymBeamShiftsControllerX.Services;

internal sealed class TelegramNotificationQueue : IAsyncDisposable
{
    private const int RetryDelayMilliseconds = 30000;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<PendingMessage> _messages = new();
    private readonly TelegramOutbox _outbox;
    private readonly Func<string, string, string, CancellationToken, Task> _send;
    private readonly Task _worker;
    private Recipient _recipient;
    private long _nextSendAt;
    private long _recipientVersion;

    public TelegramNotificationQueue(
        TelegramSettings settings, string databasePath,
        Func<string, string, string, CancellationToken, Task>? send = null)
    {
        _send = send ?? TelegramService.SendMessageAsync;
        _recipient = Recipient.From(settings);
        _outbox = new TelegramOutbox(databasePath);
        try
        {
            RemoveStaleConfirmations();
        }
        catch
        {
            _outbox.Dispose();
            throw;
        }

        _worker = Task.Run(WorkAsync);
    }

    public long RecipientVersion
    {
        get
        {
            lock (_gate)
                return _recipientVersion;
        }
    }

    public void UpdateRecipient(TelegramSettings settings)
    {
        var recipient = Recipient.From(settings);
        lock (_gate)
        {
            if (_recipient == recipient)
                return;

            _recipient = recipient;
            _recipientVersion++;
            _nextSendAt = 0;
            while (_messages.TryDequeue(out var pending))
                pending.Completion.TrySetResult(false);

            RemoveStaleConfirmations();
            WakeWorker();
        }
    }

    public Task<bool> Enqueue(string message, long? recipientVersion = null)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_recipient.IsConfigured || (recipientVersion.HasValue && recipientVersion != _recipientVersion))
                return Task.FromResult(false);

            _messages.Enqueue(new PendingMessage(message, completion));
            WakeWorker();
        }

        return completion.Task;
    }

    public void EnqueueConfirmation(string message, bool important, TimingSettings timing, long recipientVersion)
    {
        int count = important ? timing.ImportantShiftNotificationCount : 1;
        if (count <= 0)
            return;

        int interval = important ? Math.Max(0, timing.ImportantShiftNotificationDelayMilliseconds) : 0;
        int cooldown = important ? 0 : Math.Max(0, timing.TelegramDelayMilliseconds);
        lock (_gate)
        {
            if (!_recipient.IsConfigured || recipientVersion != _recipientVersion)
            {
                Logger.Log("Підтвердження зміни пропущено після зміни одержувача Telegram.");
                return;
            }

            try
            {
                _outbox.Enqueue(_recipient.Key, message, count, interval, cooldown);
            }
            catch (SqliteException ex)
            {
                Logger.Log($"Не вдалося зберегти підтвердження зміни в SQLite (код {ex.SqliteErrorCode}). Додаємо в оперативну чергу.");
                _messages.Enqueue(new PendingMessage(message, new(TaskCreationOptions.RunContinuationsAsynchronously)));
            }

            WakeWorker();
        }
    }

    private async Task WorkAsync()
    {
        CancellationToken stopping = _stop.Token;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await SendNextAsync(stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (SqliteException ex)
            {
                Logger.Log($"Помилка черги Telegram SQLite (код {ex.SqliteErrorCode}).");
                try
                {
                    await Task.Delay(RetryDelayMilliseconds, stopping);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task SendNextAsync(CancellationToken stopping)
    {
        TelegramOutbox.Confirmation? confirmation = null;
        PendingMessage? pending = null;
        Task<DeliveryResult>? delivery = null;
        TimeSpan wait = Timeout.InfiniteTimeSpan;
        long recipientVersion;

        lock (_gate)
        {
            stopping.ThrowIfCancellationRequested();
            recipientVersion = _recipientVersion;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var next = _outbox.Next(_recipient.Key, now);
            if (_nextSendAt > now)
            {
                wait = TimeSpan.FromMilliseconds(_nextSendAt - now);
            }
            else if (next is { IsRepeat: false } && next.Due <= now)
            {
                confirmation = next;
            }
            else if (!_messages.TryDequeue(out pending) && next != null)
            {
                if (next.Due <= now)
                    confirmation = next;
                else
                    wait = TimeSpan.FromMilliseconds(next.Due - now);
            }

            string? message = confirmation?.Message ?? pending?.Message;
            if (message != null)
                delivery = DeliverAsync(_recipient, message, stopping);
        }

        if (delivery == null)
        {
            await _changed.WaitAsync(wait, stopping);
            return;
        }

        DeliveryResult result;
        try
        {
            result = await delivery;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            pending?.Completion.TrySetResult(false);
            throw;
        }
        lock (_gate)
        {
            bool stillCurrent = recipientVersion == _recipientVersion;
            pending?.Completion.TrySetResult(stillCurrent && result == DeliveryResult.Sent);
            if (!stillCurrent || confirmation == null)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (result == DeliveryResult.Retry)
            {
                _outbox.Reschedule(confirmation.Id, now + RetryDelayMilliseconds, confirmation.Remaining, confirmation.IsRepeat);
            }
            else if (result == DeliveryResult.Sent && confirmation.Remaining > 1)
            {
                _outbox.Reschedule(confirmation.Id, now + confirmation.IntervalMilliseconds, confirmation.Remaining - 1, true);
            }
            else
            {
                _outbox.Remove(confirmation.Id);
            }

            if (result == DeliveryResult.Sent)
                _nextSendAt = now + confirmation.CooldownMilliseconds;
        }
    }

    private async Task<DeliveryResult> DeliverAsync(Recipient recipient, string message, CancellationToken stopping)
    {
        try
        {
            await _send(recipient.Token, recipient.ChatId, message, stopping);
            return DeliveryResult.Sent;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (IsPermanentFailure(ex.StatusCode))
        {
            Logger.Log($"Telegram відхилив повідомлення (HTTP {(int?)ex.StatusCode}).");
            return DeliveryResult.Rejected;
        }
        catch (Exception ex)
        {
            Logger.Log($"Не вдалося надіслати повідомлення в Telegram ({ex.GetType().Name}).");
            return DeliveryResult.Retry;
        }
    }

    private static bool IsPermanentFailure(HttpStatusCode? status) =>
        status is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError
            and not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests;

    private void RemoveStaleConfirmations()
    {
        int removed = _outbox.RemoveOtherRecipients(_recipient.Key);
        if (removed > 0)
            Logger.Log($"Скасовано підтвердження Telegram після зміни одержувача: {removed}.");
    }

    private void WakeWorker()
    {
        if (_changed.CurrentCount == 0)
            _changed.Release();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _worker;

        lock (_gate)
        {
            while (_messages.TryDequeue(out var pending))
                pending.Completion.TrySetResult(false);

            _outbox.Dispose();
            _changed.Dispose();
            _stop.Dispose();
        }
    }

    private sealed record Recipient(string Token, string ChatId, string Key)
    {
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(ChatId);

        public static Recipient From(TelegramSettings settings)
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(new[] { settings.BotToken, settings.ChatId });
            return new Recipient(settings.BotToken, settings.ChatId, Convert.ToHexString(SHA256.HashData(data)));
        }
    }

    private sealed record PendingMessage(string Message, TaskCompletionSource<bool> Completion);
    private enum DeliveryResult { Sent, Retry, Rejected }
}
