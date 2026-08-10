using GymBeam.AdminManager.Credentials;
using GymBeam.AdminManager.Registry;
using static GymBeam.AdminManager.Tests.DockerStatusReaderTestSupport;

namespace GymBeam.AdminManager.Tests;

public class AtomicCredentialTransactionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"manager-credential-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task PrepareAsync_CreatesExactBackupAtomicallyReplacesEnvAndPersistsRecoveryJournal()
    {
        string instances = Path.Combine(_directory, "instances");
        string storage = Path.Combine(_directory, "storage");
        ManagedBot bot = CreateBotIn(instances);
        const string original = "GYMBEAM_AUTH_LOGIN=old\nGYMBEAM_ADMIN_PORT=8080\n";
        await File.WriteAllTextAsync(Path.Combine(bot.InstancePath, ".env"), original);
        var store = new AtomicCredentialTransactionStore(storage, instances, TimeProvider.System);

        PreparedCredentialTransaction transaction = await store.PrepareAsync(
            bot,
            new Dictionary<string, string>
            {
                [CredentialKeys.GymBeamLogin] = "new=value with spaces \"quotes\" Україна"
            });

        string replaced = await File.ReadAllTextAsync(Path.Combine(bot.InstancePath, ".env"));
        Assert.Equal(
            "new=value with spaces \"quotes\" Україна",
            CredentialEnvDocument.Parse(replaced).GetValue(CredentialKeys.GymBeamLogin));
        string[] backups = Directory.GetFiles(
            Path.Combine(storage, "credential-backups", "bot1"),
            "*.env");
        Assert.Equal(original, await File.ReadAllTextAsync(Assert.Single(backups)));
        Assert.Single(await store.GetPendingAsync());
        Assert.DoesNotContain("new=value", await ReadJournalAsync(storage), StringComparison.Ordinal);
        Assert.DoesNotContain("old", await ReadJournalAsync(storage), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.CompleteAsync(
            transaction with { TransactionId = Guid.NewGuid().ToString("N") }));
        Assert.Single(await store.GetPendingAsync());

        await store.RestoreAsync(bot, transaction);
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(bot.InstancePath, ".env")));
        await store.CompleteAsync(transaction);
        Assert.Empty(await store.GetPendingAsync());

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(bot.InstancePath, ".env")));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Assert.Single(backups)));
        }
    }

    [Fact]
    public async Task PrepareAsync_RejectsRegistryPathOutsideInstancesRootBeforeReadingFile()
    {
        string instances = Path.Combine(_directory, "instances");
        string storage = Path.Combine(_directory, "storage");
        ManagedBot bot = CreateBot("bot1") with { InstancePath = Path.Combine(_directory, "outside") };
        Directory.CreateDirectory(bot.InstancePath);
        await File.WriteAllTextAsync(Path.Combine(bot.InstancePath, ".env"), "secret=must-not-read");
        var store = new AtomicCredentialTransactionStore(storage, instances, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PrepareAsync(
            bot,
            new Dictionary<string, string> { [CredentialKeys.GymBeamLogin] = "new" }));

        Assert.False(Directory.Exists(Path.Combine(storage, "credential-backups")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ManagedBot CreateBotIn(string instances)
    {
        string instancePath = Path.Combine(instances, "bot1");
        Directory.CreateDirectory(instancePath);
        return CreateBot("bot1") with { InstancePath = instancePath };
    }

    private static async Task<string> ReadJournalAsync(string storage)
    {
        string path = Assert.Single(Directory.GetFiles(
            Path.Combine(storage, "credential-transactions"),
            "*.json"));
        return await File.ReadAllTextAsync(path);
    }
}
