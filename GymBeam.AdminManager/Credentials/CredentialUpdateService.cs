using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Lifecycle;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Credentials;

public sealed record CredentialUpdateRequest(
    string? GymBeamLogin,
    string? GymBeamPassword,
    string? TelegramBotToken,
    string? TelegramChatId,
    string? BotAdminUser,
    string? BotAdminPassword,
    string? BotAdminTokenSecret);

public sealed record CredentialUpdateResult(
    string BotId,
    string Outcome,
    string Message,
    DateTimeOffset UpdatedAtUtc);

public sealed class CredentialUpdateService(
    IManagedBotRegistry registry,
    ICredentialTransactionStore store,
    IDockerLifecycleController docker,
    AuditLogger audit,
    BotOperationCoordinator coordinator,
    TimeProvider timeProvider)
{
    public async Task<CredentialUpdateResult> UpdateAsync(
        string botId,
        CredentialUpdateRequest request,
        string actor,
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        ManagedBot? bot = await FindBotAsync(botId, cancellationToken);
        if (bot is null)
        {
            return await FinishAsync(botId, "bot_not_found", "Bot was not found", actor, remoteAddress);
        }

        if (!bot.Enabled || !string.Equals(bot.LifecycleState, "active", StringComparison.Ordinal))
        {
            return await FinishAsync(bot.Id, "bot_disabled", "Bot is disabled", actor, remoteAddress);
        }

        IReadOnlyDictionary<string, string> updates;
        try
        {
            updates = CreateUpdates(request);
        }
        catch (ArgumentException)
        {
            return await FinishAsync(bot.Id, "invalid_request", "Credential update is invalid", actor, remoteAddress);
        }

        if (updates.Count == 0)
        {
            return await FinishAsync(bot.Id, "no_changes", "No credential changes were supplied", actor, remoteAddress);
        }

        using IDisposable? lease = await coordinator.TryAcquireAsync(bot.Id, cancellationToken);
        if (lease is null)
        {
            return await FinishAsync(bot.Id, "busy", "Another bot operation is in progress", actor, remoteAddress);
        }

        PreparedCredentialTransaction transaction;
        try
        {
            transaction = await store.PrepareAsync(bot, updates, cancellationToken);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return await FinishAsync(bot.Id, "storage_failure", "Credential storage update failed", actor, remoteAddress);
        }

        DockerLifecycleResult restart;
        try
        {
            restart = await docker.ExecuteAsync(
                bot,
                BotLifecycleAction.Restart,
                CancellationToken.None);
        }
        catch
        {
            return await RollbackAsync(bot, transaction, actor, remoteAddress);
        }
        if (restart.IsSuccess)
        {
            try
            {
                await store.CompleteAsync(transaction, CancellationToken.None);
                return await FinishAsync(bot.Id, "succeeded", "Credentials updated and bot is healthy", actor, remoteAddress);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                return await RollbackAsync(bot, transaction, actor, remoteAddress);
            }
        }

        return await RollbackAsync(bot, transaction, actor, remoteAddress);
    }

    private async Task<CredentialUpdateResult> RollbackAsync(
        ManagedBot bot,
        PreparedCredentialTransaction transaction,
        string actor,
        string remoteAddress)
    {
        try
        {
            await store.RestoreAsync(bot, transaction, CancellationToken.None);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return await FinishAsync(bot.Id, "rollback_failed", "Automatic credential rollback failed", actor, remoteAddress);
        }

        DockerLifecycleResult rollbackRestart;
        try
        {
            rollbackRestart = await docker.ExecuteAsync(
                bot,
                BotLifecycleAction.Restart,
                CancellationToken.None);
        }
        catch
        {
            return await FinishAsync(bot.Id, "rollback_restart_failed", "Credentials were restored but bot recovery failed", actor, remoteAddress);
        }
        if (!rollbackRestart.IsSuccess)
        {
            return await FinishAsync(bot.Id, "rollback_restart_failed", "Credentials were restored but bot recovery failed", actor, remoteAddress);
        }

        try
        {
            await store.CompleteAsync(transaction, CancellationToken.None);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return await FinishAsync(bot.Id, "rollback_cleanup_failed", "Credentials were restored but recovery remains pending", actor, remoteAddress);
        }

        return await FinishAsync(bot.Id, "rolled_back", "Credential update failed and was rolled back", actor, remoteAddress);
    }

    private static IReadOnlyDictionary<string, string> CreateUpdates(CredentialUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var updates = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(CredentialKeys.GymBeamLogin, request.GymBeamLogin);
        Add(CredentialKeys.GymBeamPassword, request.GymBeamPassword);
        Add(CredentialKeys.TelegramToken, request.TelegramBotToken);
        Add(CredentialKeys.TelegramChatId, request.TelegramChatId);
        Add(CredentialKeys.BotAdminUser, request.BotAdminUser);
        Add(CredentialKeys.BotAdminPassword, request.BotAdminPassword);
        Add(CredentialKeys.BotAdminTokenSecret, request.BotAdminTokenSecret);
        return updates;

        void Add(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (value.Length > 4096 || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                throw new ArgumentException("Credential value is invalid.");
            }

            updates[key] = value;
        }
    }

    private async Task<ManagedBot?> FindBotAsync(string botId, CancellationToken cancellationToken)
    {
        if (!BotIdValidator.IsValid(botId))
        {
            return null;
        }

        ManagedBot[] matches = (await registry.GetAllAsync(cancellationToken))
            .Where(bot => string.Equals(bot.Id, botId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private async Task<CredentialUpdateResult> FinishAsync(
        string botId,
        string outcome,
        string message,
        string actor,
        string remoteAddress)
    {
        await audit.WriteAsync(
            "bot.credentials.update",
            outcome,
            actor,
            BotIdValidator.IsValid(botId) ? botId : null,
            remoteAddress,
            CancellationToken.None);
        return new CredentialUpdateResult(botId, outcome, message, timeProvider.GetUtcNow());
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or InvalidOperationException
            or ArgumentException;
}
