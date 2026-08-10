using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Logs;

public sealed record BotLogView(string BotId, int Tail, string Outcome, string Logs);

public sealed class BotLogService(
    IManagedBotRegistry registry,
    IDockerLogReader docker,
    AuditLogger audit)
{
    public async Task<BotLogView> ReadAsync(
        string botId,
        int tail,
        string actor,
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        if (!BotIdValidator.IsValid(botId) || tail is < 1 or > 500)
        {
            await AuditAsync(
                "invalid_request",
                actor,
                BotIdValidator.IsValid(botId) ? botId : null,
                remoteAddress);
            return new BotLogView(botId, tail, "invalid_request", string.Empty);
        }

        ManagedBot[] matches = (await registry.GetAllAsync(cancellationToken))
            .Where(bot => string.Equals(bot.Id, botId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            await AuditAsync("bot_not_found", actor, botId, remoteAddress);
            return new BotLogView(botId, tail, "bot_not_found", string.Empty);
        }

        DockerLogResult result = await docker.ReadAsync(matches[0], tail, cancellationToken);
        await AuditAsync(result.Outcome, actor, botId, remoteAddress);
        return new BotLogView(botId, tail, result.Outcome, result.Logs);
    }

    private Task AuditAsync(string outcome, string actor, string? target, string remoteAddress) =>
        audit.WriteAsync(
            "bot.logs.view",
            outcome,
            actor,
            target,
            remoteAddress,
            CancellationToken.None);
}
