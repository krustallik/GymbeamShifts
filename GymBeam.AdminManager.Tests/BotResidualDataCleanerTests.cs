using GymBeam.AdminManager.Provisioning;

namespace GymBeam.AdminManager.Tests;

public sealed class BotResidualDataCleanerTests : IDisposable
{
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"bot-residual-{Guid.NewGuid():N}");

    [Fact]
    public async Task RemoveAsync_DeletesLegacyBackupsAndCredentialTransactionForBotOnly()
    {
        string legacyBackup = Path.Combine(_storage, "backups", "bot1");
        string credentialBackup = Path.Combine(_storage, "credential-backups", "bot1");
        string otherBackup = Path.Combine(_storage, "backups", "bot2");
        string transactions = Path.Combine(_storage, "credential-transactions");
        Directory.CreateDirectory(legacyBackup);
        Directory.CreateDirectory(credentialBackup);
        Directory.CreateDirectory(otherBackup);
        Directory.CreateDirectory(transactions);
        File.WriteAllText(Path.Combine(legacyBackup, ".env"), "secret");
        File.WriteAllText(Path.Combine(credentialBackup, "old.env"), "secret");
        File.WriteAllText(Path.Combine(otherBackup, ".env"), "keep");
        File.WriteAllText(Path.Combine(transactions, "bot1.json"), "{}");

        ResourceResult result = await new BotResidualDataCleaner(_storage).RemoveAsync("bot1");

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(legacyBackup));
        Assert.False(Directory.Exists(credentialBackup));
        Assert.False(File.Exists(Path.Combine(transactions, "bot1.json")));
        Assert.True(Directory.Exists(otherBackup));
    }

    [Fact]
    public async Task RemoveAsync_RejectsTraversalWithoutDeletingAnything()
    {
        string marker = Path.Combine(_storage, "backups", "bot1", "marker");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "keep");

        ResourceResult result = await new BotResidualDataCleaner(_storage).RemoveAsync("../bot1");

        Assert.False(result.Succeeded);
        Assert.True(File.Exists(marker));
    }

    public void Dispose()
    {
        if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
    }
}
