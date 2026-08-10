using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Docker;

internal sealed class UnavailableDockerStatusReader : IDockerStatusReader
{
    public Task<IReadOnlyDictionary<string, BotRuntimeStatus>> GetStatusesAsync(
        IReadOnlyList<ManagedBot> bots,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, BotRuntimeStatus> statuses = bots.ToDictionary(
            bot => bot.Id,
            _ => BotRuntimeStatus.Unknown("docker-not-configured"),
            StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(statuses);
    }
}
