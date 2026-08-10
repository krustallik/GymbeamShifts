using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Credentials;

public sealed record PreparedCredentialTransaction(string BotId, string TransactionId);

public interface ICredentialTransactionStore
{
    Task<PreparedCredentialTransaction> PrepareAsync(
        ManagedBot bot,
        IReadOnlyDictionary<string, string> updates,
        CancellationToken cancellationToken = default);

    Task RestoreAsync(
        ManagedBot bot,
        PreparedCredentialTransaction transaction,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        PreparedCredentialTransaction transaction,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PreparedCredentialTransaction>> GetPendingAsync(
        CancellationToken cancellationToken = default);
}
