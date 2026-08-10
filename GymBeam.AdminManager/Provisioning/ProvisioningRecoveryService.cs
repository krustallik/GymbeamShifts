using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Lifecycle;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Provisioning;

public sealed class ProvisioningRecoveryService(
    IManagedBotRegistry registry,
    IProvisioningResources resources,
    IProvisioningTransactionStore transactions,
    AuditLogger audit,
    BotOperationCoordinator coordinator)
{
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        foreach (ProvisioningSpec spec in await transactions.GetPendingAsync(cancellationToken))
        {
            using IDisposable? lease = await coordinator.TryAcquireAsync(spec.BotId, cancellationToken);
            if (lease is null)
            {
                continue;
            }


            IReadOnlyList<ManagedBot> bots = await registry.GetAllAsync(cancellationToken);
            if (bots.Any(bot => string.Equals(bot.Id, spec.BotId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(bot.PublicHost, spec.PublicHost, StringComparison.OrdinalIgnoreCase)
                && bot.Enabled
                && string.Equals(bot.LifecycleState, "active", StringComparison.Ordinal)))
            {
                await transactions.CompleteAsync(spec.BotId, cancellationToken);
                continue;
            }

            ResourceResult route = await TryAsync(() => resources.RemoveRouteAsync(spec, cancellationToken));
            ResourceResult container = await TryAsync(() => resources.RemoveContainerAsync(spec, cancellationToken));
            ResourceResult files = await TryAsync(() => resources.RemoveFilesAsync(spec, cancellationToken));
            bool succeeded = route.Succeeded && container.Succeeded && files.Succeeded;
            if (succeeded)
            {
                await transactions.CompleteAsync(spec.BotId, cancellationToken);
            }

            await audit.WriteAsync(
                "bot.provision.recovery",
                succeeded ? "rolled_back" : "rollback_failed",
                actor: null,
                spec.BotId,
                remoteAddress: null,
                cancellationToken);
        }
    }

    private static async Task<ResourceResult> TryAsync(Func<Task<ResourceResult>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception)
        {
            return ResourceResult.Failure("resource_failure");
        }
    }
}

internal sealed class ProvisioningRecoveryHostedService(ProvisioningRecoveryService recovery)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => recovery.RecoverAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
