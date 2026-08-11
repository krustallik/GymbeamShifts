using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Tests;

public class PersistentBotRegistryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"manager-registry-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task ImportAsync_RegistersExistingBotsWithoutReadingOrChangingTheirFiles()
    {
        string instancesPath = CreateExistingInstances();
        string bot1Env = Path.Combine(instancesPath, "bot1", ".env");
        DateTime beforeWriteTime = File.GetLastWriteTimeUtc(bot1Env);
        var importer = CreateImporter(instancesPath);

        IReadOnlyList<ManagedBot> imported = await importer.ImportAsync();

        Assert.Equal(2, imported.Count);
        Assert.Contains(imported, bot => bot.Id == "bot1"
            && bot.ComposeServiceName == "gymbeam-bot-1"
            && bot.ContainerName == "gymbeam-shifts-bot-1");
        Assert.Contains(imported, bot => bot.Id == "bot2"
            && bot.ComposeServiceName == "gymbeam-bot-2"
            && bot.ContainerName == "gymbeam-shifts-bot-2");
        Assert.Equal("GYMBEAM_AUTH_PASSWORD=do-not-read-or-log", await File.ReadAllTextAsync(bot1Env));
        Assert.Equal(beforeWriteTime, File.GetLastWriteTimeUtc(bot1Env));
        AssertOwnerOnlyOnUnix(Path.Combine(_directory, "storage", "bots.json"));
        AssertOwnerOnlyOnUnix(Path.Combine(_directory, "storage", "audit.jsonl"));
    }

    [Fact]
    public async Task ImportAsync_IsIdempotentAndPersistsAcrossRestart()
    {
        string instancesPath = CreateExistingInstances();
        ExistingBotsImporter importer = CreateImporter(instancesPath);

        IReadOnlyList<ManagedBot> first = await importer.ImportAsync();
        IReadOnlyList<ManagedBot> second = await importer.ImportAsync();
        var restartedRegistry = new PersistentBotRegistry(
            Path.Combine(_directory, "storage", "bots.json"),
            _clock);
        IReadOnlyList<ManagedBot> persisted = await restartedRegistry.GetAllAsync();

        Assert.Equal(2, first.Count);
        Assert.Empty(second);
        Assert.Equal(2, persisted.Count);
        string[] auditLines = await File.ReadAllLinesAsync(Path.Combine(_directory, "storage", "audit.jsonl"));
        Assert.Equal(2, auditLines.Count(line => line.Contains("bot.import", StringComparison.Ordinal)));
        Assert.All(persisted, bot =>
        {
            Assert.Equal($"{bot.Id}.mapa-svietidiel.sk", bot.PublicHost);
            Assert.Equal("active", bot.LifecycleState);
        });
    }

    [Fact]
    public async Task ImportAsync_MigratesLegacyRegistryRecordWithoutRecreatingBot()
    {
        string instancesPath = CreateExistingInstances();
        string storagePath = Path.Combine(_directory, "storage");
        var registry = new PersistentBotRegistry(Path.Combine(storagePath, "bots.json"), _clock);
        await registry.TryImportAsync(new ExistingBotDescriptor(
            "bot1", "Bot 1", "gymbeam-bot-1", "gymbeam-shifts-bot-1",
            Path.Combine(instancesPath, "bot1")));
        ExistingBotsImporter importer = new(
            instancesPath,
            "mapa-svietidiel.sk",
            registry,
            new AuditLogger(Path.Combine(storagePath, "audit.jsonl"), _clock));

        IReadOnlyList<ManagedBot> migrated = await importer.ImportAsync();

        ManagedBot bot1 = Assert.Single(await registry.GetAllAsync(), bot => bot.Id == "bot1");
        Assert.Equal("bot1.mapa-svietidiel.sk", bot1.PublicHost);
        Assert.Equal("active", bot1.LifecycleState);
        Assert.Equal("import", bot1.RegistrationSource);
        Assert.Contains(migrated, bot => bot.Id == "bot1");
    }

    [Fact]
    public async Task ImportAsync_MigratesProvisionedBotRelativeInstancePath()
    {
        string instancesPath = CreateExistingInstances();
        string dynamicPath = Path.Combine(instancesPath, "bot-ihor");
        Directory.CreateDirectory(dynamicPath);
        string storagePath = Path.Combine(_directory, "storage");
        var registry = new PersistentBotRegistry(Path.Combine(storagePath, "bots.json"), _clock);
        DateTimeOffset now = _clock.GetUtcNow();
        await registry.AddActiveAsync(new ManagedBot(
            "bot-ihor",
            "Bot Ihor",
            "gymbeam-bot-bot-ihor",
            "gymbeam-shifts-bot-ihor",
            "bot-ihor",
            Enabled: true,
            RegistrationSource: "provision",
            CreatedAtUtc: now,
            UpdatedAtUtc: now)
        {
            PublicHost = "bot-ihor.mapa-svietidiel.sk"
        });
        var importer = new ExistingBotsImporter(
            instancesPath,
            "mapa-svietidiel.sk",
            registry,
            new AuditLogger(Path.Combine(storagePath, "audit.jsonl"), _clock));

        await importer.ImportAsync();

        ManagedBot migrated = Assert.Single(
            await registry.GetAllAsync(),
            bot => bot.Id == "bot-ihor");
        Assert.Equal(Path.GetFullPath(dynamicPath), migrated.InstancePath);
        string audit = await File.ReadAllTextAsync(Path.Combine(storagePath, "audit.jsonl"));
        Assert.Contains("bot.instance-path.migrate", audit);
    }

    [Fact]
    public async Task ConcurrentImports_DoNotCreateDuplicatesOrCorruptRegistry()
    {
        string instancesPath = CreateExistingInstances();
        ExistingBotsImporter[] importers = Enumerable.Range(0, 8)
            .Select(_ => CreateImporter(instancesPath))
            .ToArray();

        IReadOnlyList<ManagedBot>[] results = await Task.WhenAll(
            importers.Select(importer => importer.ImportAsync()));
        var restartedRegistry = new PersistentBotRegistry(
            Path.Combine(_directory, "storage", "bots.json"),
            _clock);
        IReadOnlyList<ManagedBot> persisted = await restartedRegistry.GetAllAsync();

        Assert.Equal(2, results.Sum(result => result.Count));
        Assert.Equal(2, persisted.Count);
        Assert.Equal(2, persisted.Select(bot => bot.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task ImportAudit_DoesNotContainInstanceSecrets()
    {
        string instancesPath = CreateExistingInstances();
        ExistingBotsImporter importer = CreateImporter(instancesPath);

        await importer.ImportAsync();
        string audit = await File.ReadAllTextAsync(Path.Combine(_directory, "storage", "audit.jsonl"));

        Assert.DoesNotContain("do-not-read-or-log", audit);
        Assert.DoesNotContain("GYMBEAM_AUTH_PASSWORD", audit);
    }

    [Fact]
    public async Task RemoveAsync_DeletesIdentityAndAllowsSameBotIdToBeRegisteredAgain()
    {
        string instancesPath = CreateExistingInstances();
        string registryPath = Path.Combine(_directory, "storage", "bots.json");
        var registry = new PersistentBotRegistry(registryPath, _clock);
        var descriptor = new ExistingBotDescriptor(
            "bot1", "Bot 1", "gymbeam-bot-1", "gymbeam-shifts-bot-1",
            Path.Combine(instancesPath, "bot1"));
        Assert.NotNull(await registry.TryImportAsync(descriptor));

        await registry.RemoveAsync("bot1");
        ManagedBot? recreated = await registry.TryImportAsync(descriptor);

        Assert.NotNull(recreated);
        Assert.Single(await registry.GetAllAsync());
    }

    private ExistingBotsImporter CreateImporter(string instancesPath)
    {
        string storagePath = Path.Combine(_directory, "storage");
        return new ExistingBotsImporter(
            instancesPath,
            "mapa-svietidiel.sk",
            new PersistentBotRegistry(Path.Combine(storagePath, "bots.json"), _clock),
            new AuditLogger(Path.Combine(storagePath, "audit.jsonl"), _clock));
    }

    private string CreateExistingInstances()
    {
        string instancesPath = Path.Combine(_directory, "instances");
        foreach (string id in new[] { "bot1", "bot2" })
        {
            string botPath = Path.Combine(instancesPath, id);
            Directory.CreateDirectory(botPath);
            File.WriteAllText(Path.Combine(botPath, ".env"), "GYMBEAM_AUTH_PASSWORD=do-not-read-or-log");
            File.WriteAllText(Path.Combine(botPath, "appconfig.json"), "{}");
        }

        return instancesPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static void AssertOwnerOnlyOnUnix(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        UnixFileMode mode = File.GetUnixFileMode(path);
        const UnixFileMode forbidden = UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;
        Assert.Equal((UnixFileMode)0, mode & forbidden);
    }
}
