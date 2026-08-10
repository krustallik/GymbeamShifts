using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Provisioning;

public sealed class BotAdministrationRecoveryService(
    IManagedBotRegistry registry,
    BotAdministrationService administration)
{
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ManagedBot> bots = await registry.GetAllAsync(cancellationToken);
        foreach (ManagedBot bot in bots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bot.LifecycleState is "enabling" or "disabling")
            {
                await administration.DisableAsync(
                    bot.Id,
                    actor: "system",
                    remoteAddress: null,
                    cancellationToken);
            }
            else if (bot.LifecycleState is "deleting" or "delete_failed")
            {
                await administration.DeleteAsync(
                    bot.Id,
                    $"DELETE {bot.Id}",
                    actor: "system",
                    remoteAddress: null,
                    cancellationToken);
            }
        }
    }
}

internal sealed class BotAdministrationRecoveryHostedService(BotAdministrationRecoveryService recovery)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => recovery.RecoverAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
