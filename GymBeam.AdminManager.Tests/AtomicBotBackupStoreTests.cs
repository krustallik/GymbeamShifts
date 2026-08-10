using GymBeam.AdminManager.Provisioning;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Tests;

public sealed class AtomicBotBackupStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bot-backup-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateAsync_CopiesCompleteInstanceToPrivateAtomicBackup()
    {
        string instances = Path.Combine(_root, "instances");
        string backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(Path.Combine(instances, "bot3", "runtime-data"));
        await File.WriteAllTextAsync(Path.Combine(instances, "bot3", ".env"), "SECRET=value");
        await File.WriteAllTextAsync(Path.Combine(instances, "bot3", "runtime-data", "app.log"), "log");
        var store = new AtomicBotBackupStore(instances, backups, TimeProvider.System, 0);

        BotBackupResult result = await store.CreateAsync(Bot());

        Assert.True(result.Succeeded);
        Assert.NotNull(result.BackupId);
        string backupPath = Path.Combine(backups, "bot3", result.BackupId!);
        Assert.Equal("SECRET=value", await File.ReadAllTextAsync(Path.Combine(backupPath, ".env")));
        Assert.Equal("log", await File.ReadAllTextAsync(Path.Combine(backupPath, "runtime-data", "app.log")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(backups, "bot3"), ".tmp-*"));
    }

    [Fact]
    public async Task CreateAsync_MissingInstanceFailsWithoutCreatingBackup()
    {
        string instances = Path.Combine(_root, "instances");
        string backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(instances);
        var store = new AtomicBotBackupStore(instances, backups, TimeProvider.System, 0);

        BotBackupResult result = await store.CreateAsync(Bot());

        Assert.Equal("instance_not_found", result.Outcome);
        Assert.False(Directory.Exists(backups));
    }

    private static ManagedBot Bot() => new(
        "bot3", "Bot", "gymbeam-bot-bot3", "gymbeam-shifts-bot3", "bot3", true,
        "test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
