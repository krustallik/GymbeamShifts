using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Lifecycle;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Credentials;

public sealed class CredentialRecoveryService(
    IManagedBotRegistry registry,
    ICredentialTransactionStore store,
    IDockerLifecycleController docker,
    AuditLogger audit,
    BotOperationCoordinator coordinator)
{
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<PreparedCredentialTransaction> pending = await store.GetPendingAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        IReadOnlyList<ManagedBot> bots = await registry.GetAllAsync(cancellationToken);
        foreach (PreparedCredentialTransaction transaction in pending)
        {
            ManagedBot[] matches = bots
                .Where(bot => string.Equals(bot.Id, transaction.BotId, StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length != 1)
            {
                await AuditAsync("bot_not_found", transaction.BotId);
                throw new InvalidOperationException("Pending credential recovery references an unavailable bot.");
            }

            using IDisposable? lease = await coordinator.TryAcquireAsync(transaction.BotId, cancellationToken);
            if (lease is null)
            {
                await AuditAsync("busy", transaction.BotId);
                throw new InvalidOperationException("Pending credential recovery could not acquire bot lock.");
            }

            try
            {
                await store.RestoreAsync(matches[0], transaction, cancellationToken);
                DockerLifecycleResult restart;
                try
                {
                    restart = await docker.ExecuteAsync(
                        matches[0],
                        BotLifecycleAction.Restart,
                        cancellationToken);
                }
                catch
                {
                    await AuditAsync("restart_failed", transaction.BotId);
                    throw new InvalidOperationException("Pending credential recovery restart failed.");
                }
                if (!restart.IsSuccess)
                {
                    await AuditAsync("restart_failed", transaction.BotId);
                    throw new InvalidOperationException("Pending credential recovery restart failed.");
                }

                await store.CompleteAsync(transaction, cancellationToken);
                await AuditAsync("recovered", transaction.BotId);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or ArgumentException)
            {
                await AuditAsync("storage_failure", transaction.BotId);
                throw new InvalidOperationException("Pending credential recovery failed.", exception);
            }
        }
    }

    private Task AuditAsync(string outcome, string botId) =>
        audit.WriteAsync(
            "bot.credentials.recovery",
            outcome,
            actor: "system",
            target: botId,
            remoteAddress: null,
            CancellationToken.None);
}

internal sealed class CredentialRecoveryHostedService(CredentialRecoveryService recovery)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        recovery.RecoverAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
