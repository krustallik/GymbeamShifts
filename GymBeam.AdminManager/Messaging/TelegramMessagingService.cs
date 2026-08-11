using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Credentials;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Messaging;

public sealed record TelegramDeliveryResult(string BotId, string Outcome, string Message);
public sealed record TelegramBroadcastResult(string Outcome, int Sent, int Failed, IReadOnlyList<TelegramDeliveryResult> Results);

public interface ITelegramMessageSender
{
    Task<bool> SendAsync(string token, string chatId, string message, CancellationToken cancellationToken = default);
}

public sealed class TelegramApiMessageSender(HttpClient httpClient) : ITelegramMessageSender
{
    public async Task<bool> SendAsync(
        string token,
        string chatId,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)
            || token.Length > 256
            || token.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ':' and not '_' and not '-'))
        {
            return false;
        }

        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            $"https://api.telegram.org/bot{token}/sendMessage",
            new { chat_id = chatId, text = message },
            cancellationToken);
        return response.IsSuccessStatusCode;
    }
}

public sealed class TelegramMessagingService(
    IManagedBotRegistry registry,
    ITelegramMessageSender sender,
    AuditLogger audit,
    string instancesPath)
{
    private readonly string _instancesPath = Path.GetFullPath(instancesPath);

    public async Task<TelegramDeliveryResult> SendAsync(
        string botId,
        string? message,
        string actor,
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        if (!ValidMessage(message))
        {
            return new TelegramDeliveryResult(botId, "invalid_request", "Message must contain 1 to 4096 characters");
        }

        ManagedBot? bot = (await registry.GetAllAsync(cancellationToken)).SingleOrDefault(item =>
            string.Equals(item.Id, botId, StringComparison.Ordinal));
        TelegramDeliveryResult result = bot switch
        {
            null => new(botId, "bot_not_found", "Bot was not found"),
            { Enabled: false } => new(botId, "bot_disabled", "Bot is disabled"),
            _ => await DeliverAsync(bot, message!, cancellationToken)
        };
        await audit.WriteAsync("bot.telegram.send", result.Outcome, actor, bot?.Id, remoteAddress, CancellationToken.None);
        return result;
    }

    public async Task<TelegramBroadcastResult> SendAllAsync(
        string? message,
        string actor,
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        if (!ValidMessage(message))
        {
            return new TelegramBroadcastResult("invalid_request", 0, 0, []);
        }

        ManagedBot[] bots = (await registry.GetAllAsync(cancellationToken))
            .Where(bot => bot.Enabled && bot.LifecycleState == "active")
            .OrderBy(bot => bot.Id, StringComparer.Ordinal)
            .ToArray();
        var results = new List<TelegramDeliveryResult>(bots.Length);
        foreach (ManagedBot bot in bots)
        {
            results.Add(await DeliverAsync(bot, message!, cancellationToken));
        }

        int sent = results.Count(result => result.Outcome == "succeeded");
        int failed = results.Count - sent;
        string outcome = failed == 0 ? "succeeded" : sent == 0 ? "failed" : "partial_failure";
        await audit.WriteAsync("bot.telegram.broadcast", outcome, actor, null, remoteAddress, CancellationToken.None);
        return new TelegramBroadcastResult(outcome, sent, failed, results);
    }

    private async Task<TelegramDeliveryResult> DeliverAsync(
        ManagedBot bot,
        string message,
        CancellationToken cancellationToken)
    {
        string instancePath = Path.GetFullPath(bot.InstancePath);
        string expectedPrefix = _instancesPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!instancePath.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(instancePath), bot.Id, StringComparison.Ordinal))
        {
            return new(bot.Id, "identity_mismatch", "Bot instance path is invalid");
        }

        try
        {
            string envPath = Path.Combine(instancePath, ".env");
            CredentialEnvDocument credentials = CredentialEnvDocument.Parse(
                await File.ReadAllTextAsync(envPath, cancellationToken));
            string? token = credentials.GetValue(CredentialKeys.TelegramToken);
            string? chatId = credentials.GetValue(CredentialKeys.TelegramChatId);
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
            {
                (token, chatId) = await ReadAppConfigTelegramAsync(instancePath, cancellationToken);
            }
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chatId))
            {
                return new(bot.Id, "credentials_unavailable", "Telegram credentials are unavailable");
            }

            bool sent = await sender.SendAsync(token, chatId, message, cancellationToken);
            return sent
                ? new(bot.Id, "succeeded", "Message sent")
                : new(bot.Id, "telegram_failed", "Telegram rejected the message");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or HttpRequestException or OperationCanceledException)
        {
            return new(bot.Id, "telegram_failed", "Message could not be sent");
        }
    }

    private static async Task<(string? Token, string? ChatId)> ReadAppConfigTelegramAsync(
        string instancePath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(Path.Combine(instancePath, "appconfig.json"));
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("Telegram", out JsonElement telegram))
        {
            return (null, null);
        }

        string? token = telegram.TryGetProperty("BotToken", out JsonElement tokenElement)
            ? tokenElement.GetString() : null;
        string? chatId = telegram.TryGetProperty("ChatId", out JsonElement chatElement)
            ? chatElement.GetString() : null;
        return (IsPlaceholder(token) ? null : token, IsPlaceholder(chatId) ? null : chatId);
    }

    private static bool IsPlaceholder(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.StartsWith("${", StringComparison.Ordinal);

    private static bool ValidMessage(string? message) => !string.IsNullOrWhiteSpace(message) && message.Length <= 4096;
}
