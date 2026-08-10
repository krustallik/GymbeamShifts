using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Credentials;
using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Lifecycle;
using GymBeam.AdminManager.Registry;
using static GymBeam.AdminManager.Tests.DockerStatusReaderTestSupport;

namespace GymBeam.AdminManager.Tests;

public class CredentialRecoveryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"manager-credential-recovery-{Guid.NewGuid():N}");

    [Fact]
    public async Task RecoverAsync_CrashAfterReplaceBeforeRestartRestoresBackupAndRestartsOnlyAffectedBot()
    {
        string instances = Path.Combine(_directory, "instances");
        string storage = Path.Combine(_directory, "storage");
        string instance = Path.Combine(instances, "bot1");
        Directory.CreateDirectory(instance);
        ManagedBot bot = CreateBot("bot1") with { InstancePath = instance };
        const string original = "GYMBEAM_AUTH_PASSWORD=old-value\n";
        await File.WriteAllTextAsync(Path.Combine(instance, ".env"), original);
        var store = new AtomicCredentialTransactionStore(storage, instances, TimeProvider.System);
        await store.PrepareAsync(
            bot,
            new Dictionary<string, string> { [CredentialKeys.GymBeamPassword] = "new-value" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PrepareAsync(
            bot,
            new Dictionary<string, string> { [CredentialKeys.GymBeamPassword] = "must-not-overwrite-journal" }));
        var docker = new RecoveryDockerController(new DockerLifecycleResult("succeeded", "healthy"));
        var recovery = new CredentialRecoveryService(
            new FakeRegistry([bot]),
            store,
            docker,
            new AuditLogger(Path.Combine(storage, "audit.jsonl"), TimeProvider.System),
            new BotOperationCoordinator());

        await recovery.RecoverAsync();

        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(instance, ".env")));
        Assert.Equal("bot1", Assert.Single(docker.BotIds));
        Assert.Empty(await store.GetPendingAsync());
        string audit = await File.ReadAllTextAsync(Path.Combine(storage, "audit.jsonl"));
        Assert.Contains("recovered", audit);
        Assert.DoesNotContain("old-value", audit);
        Assert.DoesNotContain("new-value", audit);
    }

    [Fact]
    public async Task RecoverAsync_HealthFailureKeepsJournalForNextStartupAndFailsClosed()
    {
        string instances = Path.Combine(_directory, "instances");
        string storage = Path.Combine(_directory, "storage");
        string instance = Path.Combine(instances, "bot1");
        Directory.CreateDirectory(instance);
        ManagedBot bot = CreateBot("bot1") with { InstancePath = instance };
        await File.WriteAllTextAsync(Path.Combine(instance, ".env"), "GYMBEAM_AUTH_PASSWORD=old\n");
        var store = new AtomicCredentialTransactionStore(storage, instances, TimeProvider.System);
        await store.PrepareAsync(
            bot,
            new Dictionary<string, string> { [CredentialKeys.GymBeamPassword] = "new" });
        var recovery = new CredentialRecoveryService(
            new FakeRegistry([bot]),
            store,
            new RecoveryDockerController(new DockerLifecycleResult("health_timeout", "failed")),
            new AuditLogger(Path.Combine(storage, "audit.jsonl"), TimeProvider.System),
            new BotOperationCoordinator());

        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.RecoverAsync());

        Assert.Single(await store.GetPendingAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeRegistry(IReadOnlyList<ManagedBot> bots) : IManagedBotRegistry
    {
        public Task<IReadOnlyList<ManagedBot>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(bots);
    }

    private sealed class RecoveryDockerController(DockerLifecycleResult result) : IDockerLifecycleController
    {
        public List<string> BotIds { get; } = [];

        public Task<DockerLifecycleResult> ExecuteAsync(
            ManagedBot bot,
            BotLifecycleAction action,
            CancellationToken cancellationToken = default)
        {
            BotIds.Add(bot.Id);
            Assert.Equal(BotLifecycleAction.Restart, action);
            return Task.FromResult(result);
        }
    }
}
