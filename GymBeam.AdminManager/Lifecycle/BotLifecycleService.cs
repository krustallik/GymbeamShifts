using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Lifecycle;

public sealed record BotLifecycleOperation(
    string BotId,
    string Action,
    string Status,
    string Outcome,
    string Message,
    DateTimeOffset UpdatedAtUtc);

public sealed class BotLifecycleService(
    IManagedBotRegistry registry,
    IDockerLifecycleController docker,
    AuditLogger audit,
    TimeProvider timeProvider,
    BotOperationCoordinator coordinator)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BotLifecycleOperation> _progress = new(StringComparer.Ordinal);

    public BotLifecycleOperation? GetProgress(string botId) =>
        BotIdValidator.IsValid(botId) && _progress.TryGetValue(botId, out BotLifecycleOperation? operation)
            ? operation
            : null;

    public async Task<BotLifecycleOperation> ExecuteAsync(
        string botId,
        BotLifecycleAction action,
        string actor,
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        string actionName = action.ToString().ToLowerInvariant();
        ManagedBot? bot = await FindBotAsync(botId, cancellationToken);
        if (bot is null)
        {
            BotLifecycleOperation missing = Create(botId, actionName, "completed", "bot_not_found", "Bot was not found");
            await AuditAsync(
                actionName,
                missing.Outcome,
                actor,
                BotIdValidator.IsValid(botId) ? botId : null,
                remoteAddress);
            return missing;
        }

        if (!bot.Enabled || !string.Equals(bot.LifecycleState, "active", StringComparison.Ordinal))
        {
            BotLifecycleOperation disabled = Create(
                bot.Id,
                actionName,
                "completed",
                "bot_disabled",
                "Bot is disabled");
            await AuditAsync(actionName, disabled.Outcome, actor, bot.Id, remoteAddress);
            return disabled;
        }

        using IDisposable? lease = await coordinator.TryAcquireAsync(bot.Id, cancellationToken);
        if (lease is null)
        {
            BotLifecycleOperation busy = Create(bot.Id, actionName, "in_progress", "busy", "Another lifecycle operation is in progress");
            await AuditAsync(actionName, busy.Outcome, actor, bot.Id, remoteAddress);
            return busy;
        }

        _progress[bot.Id] = Create(bot.Id, actionName, "in_progress", "in_progress", "Operation is in progress");
        DockerLifecycleResult result = await docker.ExecuteAsync(bot, action, cancellationToken);
        BotLifecycleOperation completed = Create(bot.Id, actionName, "completed", result.Outcome, result.Message);
        _progress[bot.Id] = completed;
        await AuditAsync(actionName, result.Outcome, actor, bot.Id, remoteAddress);
        return completed;
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

    private BotLifecycleOperation Create(
        string botId,
        string action,
        string status,
        string outcome,
        string message) =>
        new(botId, action, status, outcome, message, timeProvider.GetUtcNow());

    private Task AuditAsync(
        string action,
        string outcome,
        string actor,
        string? target,
        string remoteAddress) =>
        audit.WriteAsync(
            $"bot.lifecycle.{action}",
            outcome,
            actor,
            target,
            remoteAddress,
            CancellationToken.None);
}
