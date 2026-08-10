using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Docker;

public sealed record DockerLogResult(string Outcome, string Logs);

public interface IDockerLogReader
{
    Task<DockerLogResult> ReadAsync(
        ManagedBot bot,
        int tail,
        CancellationToken cancellationToken = default);
}
