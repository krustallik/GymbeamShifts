using System.Text;
using System.Text.Json;
using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Registry;
using GymBeam.AdminManager.Storage;

namespace GymBeam.AdminManager.Credentials;

public sealed class AtomicCredentialTransactionStore : ICredentialTransactionStore
{
    private const int MaximumEnvBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _storageRoot;
    private readonly string _instancesRoot;
    private readonly TimeProvider _timeProvider;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public AtomicCredentialTransactionStore(
        string storageRoot,
        string instancesRoot,
        TimeProvider timeProvider)
    {
        _storageRoot = Path.GetFullPath(storageRoot);
        _instancesRoot = Path.GetFullPath(instancesRoot);
        _timeProvider = timeProvider;
    }

    public async Task<PreparedCredentialTransaction> PrepareAsync(
        ManagedBot bot,
        IReadOnlyDictionary<string, string> updates,
        CancellationToken cancellationToken = default)
    {
        string envPath = ValidateAndGetEnvPath(bot);
        string journalPath = GetJournalPath(bot.Id);
        if (File.Exists(journalPath))
        {
            throw new InvalidOperationException("Bot has a pending credential recovery.");
        }

        byte[] original = await ReadLimitedAsync(envPath, cancellationToken);
        string updatedText = CredentialEnvDocument.Parse(new UTF8Encoding(false, true).GetString(original))
            .Apply(updates)
            .Serialize();
        byte[] updated = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(updatedText);
        if (updated.Length > MaximumEnvBytes)
        {
            throw new InvalidDataException("Credential environment is too large.");
        }

        string transactionId = Guid.NewGuid().ToString("N");
        var transaction = new PreparedCredentialTransaction(bot.Id, transactionId);
        string backupPath = GetBackupPath(transaction);
        StoragePermissions.EnsureDirectory(Path.GetDirectoryName(backupPath)!);
        StoragePermissions.EnsureDirectory(Path.GetDirectoryName(journalPath)!);

        try
        {
            await WriteNewDurableAsync(backupPath, original, cancellationToken);
        }
        catch
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            throw;
        }
        var journal = new JournalDocument(bot.Id, transactionId, _timeProvider.GetUtcNow());
        await AtomicWriteAsync(
            journalPath,
            JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions),
            cancellationToken);
        try
        {
            await AtomicWriteAsync(envPath, updated, cancellationToken);
        }
        catch
        {
            try
            {
                await AtomicWriteAsync(envPath, original, CancellationToken.None);
                File.Delete(journalPath);
            }
            catch
            {
                // The durable journal and backup intentionally remain for startup recovery.
            }

            throw;
        }

        return transaction;
    }

    public async Task RestoreAsync(
        ManagedBot bot,
        PreparedCredentialTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        ValidateTransaction(bot.Id, transaction);
        string envPath = ValidateAndGetEnvPath(bot);
        JournalDocument journal = await ReadJournalAsync(bot.Id, cancellationToken);
        if (!string.Equals(journal.TransactionId, transaction.TransactionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Credential transaction journal does not match.");
        }

        byte[] backup = await ReadLimitedAsync(GetBackupPath(transaction), cancellationToken);
        await AtomicWriteAsync(envPath, backup, cancellationToken);
    }

    public async Task CompleteAsync(
        PreparedCredentialTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTransaction(transaction.BotId, transaction);
        string journalPath = GetJournalPath(transaction.BotId);
        if (!File.Exists(journalPath))
        {
            throw new InvalidDataException("Credential transaction journal is missing.");
        }

        JournalDocument journal = await ReadJournalAsync(transaction.BotId, cancellationToken);
        if (!string.Equals(journal.TransactionId, transaction.TransactionId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Credential transaction journal does not match.");
        }

        File.Delete(journalPath);
    }

    public async Task<IReadOnlyList<PreparedCredentialTransaction>> GetPendingAsync(
        CancellationToken cancellationToken = default)
    {
        string journalDirectory = Path.Combine(_storageRoot, "credential-transactions");
        if (!Directory.Exists(journalDirectory))
        {
            return [];
        }

        var pending = new List<PreparedCredentialTransaction>();
        foreach (string path in Directory.GetFiles(journalDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string botId = Path.GetFileNameWithoutExtension(path);
            if (!BotIdValidator.IsValid(botId))
            {
                throw new InvalidDataException("Invalid credential transaction journal name.");
            }

            JournalDocument journal = await ReadJournalAsync(botId, cancellationToken);
            var transaction = new PreparedCredentialTransaction(journal.BotId, journal.TransactionId);
            ValidateTransaction(botId, transaction);
            pending.Add(transaction);
        }

        return pending;
    }

    private string ValidateAndGetEnvPath(ManagedBot bot)
    {
        if (!BotIdValidator.IsValid(bot.Id))
        {
            throw new InvalidOperationException("Invalid bot identity.");
        }

        string expectedInstancePath = Path.GetFullPath(Path.Combine(_instancesRoot, bot.Id));
        string registeredInstancePath = Path.GetFullPath(bot.InstancePath);
        if (!string.Equals(expectedInstancePath, registeredInstancePath, _pathComparison)
            || !Directory.Exists(expectedInstancePath)
            || File.GetAttributes(expectedInstancePath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Bot instance path is outside the managed root.");
        }

        string envPath = Path.Combine(expectedInstancePath, ".env");
        if (!File.Exists(envPath) || File.GetAttributes(envPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("Bot credential environment is unavailable.");
        }

        return envPath;
    }

    private async Task<JournalDocument> ReadJournalAsync(
        string botId,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadLimitedAsync(GetJournalPath(botId), cancellationToken);
        return JsonSerializer.Deserialize<JournalDocument>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Credential transaction journal is invalid.");
    }

    private static async Task<byte[]> ReadLimitedAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumEnvBytes)
        {
            throw new InvalidDataException("Credential transaction file is missing or too large.");
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private async Task AtomicWriteAsync(
        string destination,
        byte[] content,
        CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Credential path has no parent directory.");
        string temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteNewDurableAsync(temporary, content, cancellationToken);
            File.Move(temporary, destination, overwrite: true);
            StoragePermissions.EnsureFile(destination);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task WriteNewDurableAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        StoragePermissions.EnsureFile(path);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    private string GetBackupPath(PreparedCredentialTransaction transaction) =>
        Path.Combine(
            _storageRoot,
            "credential-backups",
            transaction.BotId,
            $"{transaction.TransactionId}.env");

    private string GetJournalPath(string botId) =>
        Path.Combine(_storageRoot, "credential-transactions", $"{botId}.json");

    private static void ValidateTransaction(string expectedBotId, PreparedCredentialTransaction transaction)
    {
        if (!BotIdValidator.IsValid(expectedBotId)
            || !string.Equals(expectedBotId, transaction.BotId, StringComparison.Ordinal)
            || !Guid.TryParseExact(transaction.TransactionId, "N", out _))
        {
            throw new InvalidDataException("Credential transaction identity is invalid.");
        }
    }

    private sealed record JournalDocument(
        string BotId,
        string TransactionId,
        DateTimeOffset CreatedAtUtc);
}
