using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Docker;

public interface IDockerStatusReader
{
    Task<IReadOnlyDictionary<string, BotRuntimeStatus>> GetStatusesAsync(
        IReadOnlyList<ManagedBot> bots,
        CancellationToken cancellationToken = default);
}

public sealed record BotRuntimeStatus(
    string State,
    string Health,
    TimeSpan? Uptime,
    DateTimeOffset? LastUpdatedAtUtc,
    string Availability)
{
    public static BotRuntimeStatus Unknown(string reason = "unknown")
    {
        _ = reason;
        return new BotRuntimeStatus(
            "unknown",
            "unknown",
            Uptime: null,
            LastUpdatedAtUtc: null,
            Availability: "unknown");
    }
}
