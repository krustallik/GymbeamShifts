using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Docker;

public enum BotLifecycleAction
{
    Start,
    Stop,
    Restart
}

public sealed record DockerLifecycleResult(string Outcome, string Message)
{
    public bool IsSuccess => Outcome is "succeeded" or "already_running" or "already_stopped";
}

public interface IDockerLifecycleController
{
    Task<DockerLifecycleResult> ExecuteAsync(
        ManagedBot bot,
        BotLifecycleAction action,
        CancellationToken cancellationToken = default);
}
