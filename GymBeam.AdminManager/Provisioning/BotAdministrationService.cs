using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Lifecycle;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Provisioning;

public sealed record BotAdministrationResult(string Outcome);

public sealed class BotAdministrationService(
    IManagedBotRegistryMutations registry,
    IProvisioningResources resources,
    IDockerLifecycleController lifecycle,
    IBotResidualDataCleaner residualData,
    BotOperationCoordinator coordinator,
    AuditLogger audit,
    TimeProvider timeProvider,
    string baseDomain)
{
    public Task<BotAdministrationResult> DisableAsync(
        string botId,
        string? actor,
        string? remoteAddress,
        CancellationToken cancellationToken = default) =>
        SetEnabledAsync(botId, enabled: false, actor, remoteAddress, cancellationToken);

    public Task<BotAdministrationResult> EnableAsync(
        string botId,
        string? actor,
        string? remoteAddress,
        CancellationToken cancellationToken = default) =>
        SetEnabledAsync(botId, enabled: true, actor, remoteAddress, cancellationToken);

    public async Task<BotAdministrationResult> DeleteAsync(
        string botId,
        string? confirmation,
        string? actor,
        string? remoteAddress,
        CancellationToken cancellationToken = default)
    {
        if (!IsBotId(botId) || !string.Equals(confirmation, $"DELETE {botId}", StringComparison.Ordinal))
        {
            return new BotAdministrationResult("confirmation_required");
        }

        ManagedBot? bot = await FindAsync(botId, cancellationToken);
        if (bot is null)
        {
            return new BotAdministrationResult("bot_not_found");
        }

        using IDisposable? lease = await coordinator.TryAcquireAsync(botId, cancellationToken);
        if (lease is null)
        {
            return new BotAdministrationResult("operation_in_progress");
        }

        bot = bot with
        {
            Enabled = false,
            UpdatedAtUtc = timeProvider.GetUtcNow(),
            LifecycleState = "deleting"
        };
        await registry.UpdateAsync(bot, cancellationToken);
        ProvisioningSpec spec = ToSpec(bot);
        foreach (Func<Task<ResourceResult>> step in new Func<Task<ResourceResult>>[]
        {
            () => resources.RemoveRouteAsync(spec, cancellationToken),
            () => resources.RemoveContainerAsync(spec, cancellationToken),
            () => resources.RemoveFilesAsync(spec, cancellationToken),
            () => residualData.RemoveAsync(bot.Id, cancellationToken)
        })
        {
            ResourceResult result = await step();
            if (!result.Succeeded)
            {
                await registry.UpdateAsync(bot with
                {
                    LifecycleState = "delete_failed",
                    UpdatedAtUtc = timeProvider.GetUtcNow()
                }, cancellationToken);
                await WriteAuditAsync("bot.delete", result.Outcome, actor, botId, remoteAddress, cancellationToken);
                return new BotAdministrationResult(result.Outcome);
            }
        }

        await registry.RemoveAsync(bot.Id, cancellationToken);
        await WriteAuditAsync("bot.delete", "succeeded", actor, botId, remoteAddress, cancellationToken);
        return new BotAdministrationResult("succeeded");
    }

    private async Task<BotAdministrationResult> SetEnabledAsync(
        string botId,
        bool enabled,
        string? actor,
        string? remoteAddress,
        CancellationToken cancellationToken)
    {
        if (!IsBotId(botId))
        {
            return new BotAdministrationResult("bot_not_found");
        }

        ManagedBot? bot = await FindAsync(botId, cancellationToken);
        if (bot is null || bot.LifecycleState is "deleted" or "deleting")
        {
            return new BotAdministrationResult("bot_not_found");
        }

        if (bot.Enabled == enabled && bot.LifecycleState == (enabled ? "active" : "disabled"))
        {
            return new BotAdministrationResult(enabled ? "already_enabled" : "already_disabled");
        }

        using IDisposable? lease = await coordinator.TryAcquireAsync(botId, cancellationToken);
        if (lease is null)
        {
            return new BotAdministrationResult("operation_in_progress");
        }

        ProvisioningSpec spec = ToSpec(bot);
        ManagedBot original = bot;
        ManagedBot transitional = bot with
        {
            Enabled = false,
            LifecycleState = enabled ? "enabling" : "disabling",
            UpdatedAtUtc = timeProvider.GetUtcNow()
        };
        await registry.UpdateAsync(transitional, cancellationToken);
        string outcome;
        if (!enabled)
        {
            ResourceResult route = await resources.RemoveRouteAsync(spec, cancellationToken);
            if (!route.Succeeded)
            {
                await registry.UpdateAsync(original, cancellationToken);
                outcome = route.Outcome;
            }
            else
            {
                DockerLifecycleResult stopped = await lifecycle.ExecuteAsync(original, BotLifecycleAction.Stop, cancellationToken);
                if (stopped.Outcome is not "succeeded" and not "already_stopped")
                {
                    await resources.AddRouteAsync(spec, cancellationToken);
                    await registry.UpdateAsync(original, cancellationToken);
                    outcome = stopped.Outcome;
                }
                else
                {
                    await registry.UpdateAsync(transitional with
                    {
                        Enabled = false,
                        LifecycleState = "disabled",
                        UpdatedAtUtc = timeProvider.GetUtcNow()
                    }, cancellationToken);
                    outcome = "succeeded";
                }
            }
        }
        else
        {
            DockerLifecycleResult started = await lifecycle.ExecuteAsync(original, BotLifecycleAction.Start, cancellationToken);
            if (started.Outcome is not "succeeded" and not "already_running")
            {
                await registry.UpdateAsync(original, cancellationToken);
                outcome = started.Outcome;
            }
            else
            {
                ResourceResult route = await resources.AddRouteAsync(spec, cancellationToken);
                ResourceResult external = route.Succeeded
                    ? await resources.WaitExternalHealthAsync(spec, cancellationToken)
                    : route;
                if (!external.Succeeded)
                {
                    await resources.RemoveRouteAsync(spec, cancellationToken);
                    await lifecycle.ExecuteAsync(original, BotLifecycleAction.Stop, cancellationToken);
                    await registry.UpdateAsync(original, cancellationToken);
                    outcome = external.Outcome;
                }
                else
                {
                    await registry.UpdateAsync(transitional with
                    {
                        Enabled = true,
                        LifecycleState = "active",
                        UpdatedAtUtc = timeProvider.GetUtcNow()
                    }, cancellationToken);
                    outcome = "succeeded";
                }
            }
        }

        await WriteAuditAsync(enabled ? "bot.enable" : "bot.disable", outcome, actor, botId, remoteAddress, cancellationToken);
        return new BotAdministrationResult(outcome);
    }

    private async Task<ManagedBot?> FindAsync(string botId, CancellationToken cancellationToken) =>
        (await registry.GetAllAsync(cancellationToken)).SingleOrDefault(bot =>
            string.Equals(bot.Id, botId, StringComparison.Ordinal));

    private ProvisioningSpec ToSpec(ManagedBot bot)
    {
        string host = bot.PublicHost ?? $"{bot.Id}.{baseDomain}";
        string suffix = $".{baseDomain}";
        string subdomain = host.EndsWith(suffix, StringComparison.Ordinal)
            ? host[..^suffix.Length]
            : bot.Id;
        return new ProvisioningSpec(
            bot.Id, bot.DisplayName, subdomain, host,
            bot.ComposeServiceName, bot.ContainerName, bot.Id);
    }

    private Task WriteAuditAsync(
        string action,
        string outcome,
        string? actor,
        string botId,
        string? remoteAddress,
        CancellationToken cancellationToken) =>
        audit.WriteAsync(action, outcome, actor, botId, remoteAddress, cancellationToken);

    private static bool IsBotId(string value) => value.Length is >= 1 and <= 64
        && value[0] is >= 'a' and <= 'z'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
