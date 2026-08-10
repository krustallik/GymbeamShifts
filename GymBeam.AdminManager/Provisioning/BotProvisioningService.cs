using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Lifecycle;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Provisioning;

public sealed class BotProvisioningService(
    IManagedBotRegistryMutations registry,
    IProvisioningResources resources,
    IProvisioningTransactionStore transactions,
    AuditLogger audit,
    BotOperationCoordinator coordinator,
    TimeProvider timeProvider,
    string baseDomain)
{
    public async Task<ProvisioningResult> ProvisionAsync(
        ProvisionBotRequest request,
        string? actor,
        string? remoteAddress,
        CancellationToken cancellationToken = default)
    {
        ProvisioningSpec spec;
        try
        {
            spec = ProvisioningValidation.Validate(request, baseDomain);
        }
        catch (ArgumentException)
        {
            return new ProvisioningResult("invalid_request");
        }

        IReadOnlyList<ManagedBot> bots = await registry.GetAllAsync(cancellationToken);
        if (bots.Any(bot => string.Equals(bot.Id, spec.BotId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(bot.PublicHost, spec.PublicHost, StringComparison.OrdinalIgnoreCase)))
        {
            return new ProvisioningResult("already_exists");
        }

        using IDisposable? lease = await coordinator.TryAcquireAsync(spec.BotId, cancellationToken);
        if (lease is null || !await transactions.BeginAsync(spec, cancellationToken))
        {
            return new ProvisioningResult("operation_in_progress");
        }

        ResourceResult preflight = await TryResourceAsync(
            () => resources.PreflightAsync(spec, cancellationToken));
        if (!preflight.Succeeded)
        {
            await transactions.CompleteAsync(spec.BotId, cancellationToken);
            await audit.WriteAsync("bot.provision", preflight.Outcome, actor, spec.BotId, remoteAddress, cancellationToken);
            return new ProvisioningResult(preflight.Outcome);
        }

        foreach (Func<Task<ResourceResult>> step in new Func<Task<ResourceResult>>[]
        {
            () => resources.CreateFilesAsync(spec, request, cancellationToken),
            () => resources.CreateContainerAsync(spec, cancellationToken),
            () => resources.AddRouteAsync(spec, cancellationToken),
            () => resources.WaitInternalHealthAsync(spec, cancellationToken),
            () => resources.WaitExternalHealthAsync(spec, cancellationToken)
        })
        {
            ResourceResult result = await TryResourceAsync(step);
            if (!result.Succeeded)
            {
                return await RollbackAsync(spec, actor, remoteAddress, cancellationToken);
            }
        }

        try
        {
            await registry.AddActiveAsync(spec.ToManagedBot(timeProvider.GetUtcNow()), cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<ManagedBot> current = await registry.GetAllAsync(cancellationToken);
            bool activated = current.Any(bot => string.Equals(bot.Id, spec.BotId, StringComparison.Ordinal)
                && string.Equals(bot.PublicHost, spec.PublicHost, StringComparison.Ordinal)
                && bot.Enabled
                && bot.LifecycleState == "active");
            if (!activated)
            {
                return await RollbackAsync(spec, actor, remoteAddress, cancellationToken);
            }
        }
        await transactions.CompleteAsync(spec.BotId, cancellationToken);
        await audit.WriteAsync("bot.provision", "succeeded", actor, spec.BotId, remoteAddress, cancellationToken);
        return new ProvisioningResult("succeeded");
    }

    private async Task<ProvisioningResult> RollbackAsync(
        ProvisioningSpec spec,
        string? actor,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        ResourceResult route = await TryResourceAsync(() => resources.RemoveRouteAsync(spec, cancellationToken));
        ResourceResult container = await TryResourceAsync(() => resources.RemoveContainerAsync(spec, cancellationToken));
        ResourceResult files = await TryResourceAsync(() => resources.RemoveFilesAsync(spec, cancellationToken));
        bool succeeded = route.Succeeded && container.Succeeded && files.Succeeded;
        string outcome = succeeded ? "rolled_back" : "rollback_failed";
        if (succeeded)
        {
            await transactions.CompleteAsync(spec.BotId, cancellationToken);
        }

        await audit.WriteAsync("bot.provision", outcome, actor, spec.BotId, remoteAddress, cancellationToken);
        return new ProvisioningResult(outcome);
    }

    private static async Task<ResourceResult> TryResourceAsync(Func<Task<ResourceResult>> operation)
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
