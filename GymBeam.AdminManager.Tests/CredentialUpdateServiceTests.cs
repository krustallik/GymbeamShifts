using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Credentials;
using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Lifecycle;
using GymBeam.AdminManager.Registry;
using static GymBeam.AdminManager.Tests.DockerStatusReaderTestSupport;

namespace GymBeam.AdminManager.Tests;

public class CredentialUpdateServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"manager-credential-service-{Guid.NewGuid():N}");

    [Fact]
    public async Task UpdateAsync_BlankFieldsAreUnchangedAndOnlySelectedBotRestarts()
    {
        var store = new RecordingTransactionStore();
        var docker = new RecordingLifecycleController();
        CredentialUpdateService service = CreateService(store, docker);
        var request = new CredentialUpdateRequest(
            GymBeamLogin: "new user",
            GymBeamPassword: "",
            TelegramBotToken: null,
            TelegramChatId: "  ",
            BotAdminUser: "admin=new",
            BotAdminPassword: "",
            BotAdminTokenSecret: "unicode секрет");

        CredentialUpdateResult result = await service.UpdateAsync(
            "bot1", request, "admin", "127.0.0.1");

        Assert.Equal("succeeded", result.Outcome);
        Assert.Equal("bot1", Assert.Single(docker.BotIds));
        Assert.Equal(BotLifecycleAction.Restart, Assert.Single(docker.Actions));
        Assert.Equal(3, store.Updates.Count);
        Assert.Equal("new user", store.Updates[CredentialKeys.GymBeamLogin]);
        Assert.Equal("admin=new", store.Updates[CredentialKeys.BotAdminUser]);
        Assert.Equal("unicode секрет", store.Updates[CredentialKeys.BotAdminTokenSecret]);
        Assert.All(store.Updates.Values, value => Assert.DoesNotContain(value, result.ToString()));
        string audit = await File.ReadAllTextAsync(Path.Combine(_directory, "audit.jsonl"));
        Assert.DoesNotContain("new user", audit);
        Assert.DoesNotContain("unicode секрет", audit);
    }

    [Fact]
    public async Task UpdateAsync_ConcurrentUpdatesForSameBotFailFastAsBusy()
    {
        var store = new RecordingTransactionStore();
        var docker = new RecordingLifecycleController { Block = true };
        CredentialUpdateService service = CreateService(store, docker);
        CredentialUpdateRequest request = RequestWithPassword("one");

        Task<CredentialUpdateResult> first = service.UpdateAsync("bot1", request, "admin", "127.0.0.1");
        await docker.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        CredentialUpdateResult second = await service.UpdateAsync(
            "bot1", RequestWithPassword("two"), "admin", "127.0.0.1");
        docker.Release.TrySetResult();
        CredentialUpdateResult completed = await first;

        Assert.Equal("busy", second.Outcome);
        Assert.Equal("succeeded", completed.Outcome);
        Assert.Equal(1, store.PrepareCalls);
    }

    [Theory]
    [InlineData("disk_full")]
    [InlineData("read_only")]
    public async Task UpdateAsync_StorageFailureNeverRestartsAndReturnsControlledResult(string failure)
    {
        Exception storageException = failure == "read_only"
            ? new UnauthorizedAccessException(failure + " secret")
            : new IOException(failure + " secret");
        var store = new RecordingTransactionStore { PrepareFailure = storageException };
        var docker = new RecordingLifecycleController();
        CredentialUpdateService service = CreateService(store, docker);

        CredentialUpdateResult result = await service.UpdateAsync(
            "bot1", RequestWithPassword("not-in-result"), "admin", "127.0.0.1");

        Assert.Equal("storage_failure", result.Outcome);
        Assert.Empty(docker.BotIds);
        Assert.DoesNotContain("secret", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not-in-result", result.ToString());
    }

    [Fact]
    public async Task UpdateAsync_HealthFailureAutomaticallyRestoresBackupAndRestartsOldCredentials()
    {
        var store = new RecordingTransactionStore();
        var docker = new RecordingLifecycleController();
        docker.Results.Enqueue(new DockerLifecycleResult("health_timeout", "new credentials failed"));
        docker.Results.Enqueue(new DockerLifecycleResult("succeeded", "old credentials healthy"));
        CredentialUpdateService service = CreateService(store, docker);

        CredentialUpdateResult result = await service.UpdateAsync(
            "bot1", RequestWithPassword("bad-password"), "admin", "127.0.0.1");

        Assert.Equal("rolled_back", result.Outcome);
        Assert.Equal(1, store.RestoreCalls);
        Assert.Equal(1, store.CompleteCalls);
        Assert.Equal(2, docker.BotIds.Count);
        Assert.DoesNotContain("bad-password", result.ToString());
    }

    [Fact]
    public async Task UpdateAsync_RollbackFailureIsControlledAndKeepsPendingRecovery()
    {
        var store = new RecordingTransactionStore { RestoreFailure = new IOException("rollback secret") };
        var docker = new RecordingLifecycleController();
        docker.Results.Enqueue(new DockerLifecycleResult("health_timeout", "failed"));
        CredentialUpdateService service = CreateService(store, docker);

        CredentialUpdateResult result = await service.UpdateAsync(
            "bot1", RequestWithPassword("bad-password"), "admin", "127.0.0.1");

        Assert.Equal("rollback_failed", result.Outcome);
        Assert.Equal(0, store.CompleteCalls);
        Assert.Single(docker.BotIds);
        Assert.DoesNotContain("secret", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAsync_RollbackRestartFailureKeepsPendingRecovery()
    {
        var store = new RecordingTransactionStore();
        var docker = new RecordingLifecycleController();
        docker.Results.Enqueue(new DockerLifecycleResult("health_timeout", "new failed"));
        docker.Results.Enqueue(new DockerLifecycleResult("docker_unavailable", "old restart failed"));
        CredentialUpdateService service = CreateService(store, docker);

        CredentialUpdateResult result = await service.UpdateAsync(
            "bot1", RequestWithPassword("bad-password"), "admin", "127.0.0.1");

        Assert.Equal("rollback_restart_failed", result.Outcome);
        Assert.Equal(1, store.RestoreCalls);
        Assert.Equal(0, store.CompleteCalls);
        Assert.Equal(2, docker.BotIds.Count);
    }

    private CredentialUpdateService CreateService(
        ICredentialTransactionStore store,
        IDockerLifecycleController docker)
    {
        var registry = new FakeRegistry([CreateBot("bot1"), CreateBot("bot2")]);
        return new CredentialUpdateService(
            registry,
            store,
            docker,
            new AuditLogger(Path.Combine(_directory, "audit.jsonl"), TimeProvider.System),
            new BotOperationCoordinator(),
            TimeProvider.System);
    }

    private static CredentialUpdateRequest RequestWithPassword(string password) => new(
        GymBeamLogin: null,
        GymBeamPassword: password,
        TelegramBotToken: null,
        TelegramChatId: null,
        BotAdminUser: null,
        BotAdminPassword: null,
        BotAdminTokenSecret: null);

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

    private sealed class RecordingTransactionStore : ICredentialTransactionStore
    {
        public Dictionary<string, string> Updates { get; } = new(StringComparer.Ordinal);
        public int PrepareCalls;
        public int RestoreCalls;
        public int CompleteCalls;
        public Exception? PrepareFailure;
        public Exception? RestoreFailure;

        public Task<PreparedCredentialTransaction> PrepareAsync(
            ManagedBot bot,
            IReadOnlyDictionary<string, string> updates,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PrepareCalls);
            if (PrepareFailure is not null)
            {
                throw PrepareFailure;
            }

            foreach ((string key, string value) in updates)
            {
                Updates[key] = value;
            }

            return Task.FromResult(new PreparedCredentialTransaction(bot.Id, "transaction-id"));
        }

        public Task RestoreAsync(
            ManagedBot bot,
            PreparedCredentialTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref RestoreCalls);
            return RestoreFailure is null ? Task.CompletedTask : Task.FromException(RestoreFailure);
        }

        public Task CompleteAsync(
            PreparedCredentialTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CompleteCalls);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PreparedCredentialTransaction>> GetPendingAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PreparedCredentialTransaction>>([]);
    }

    private sealed class RecordingLifecycleController : IDockerLifecycleController
    {
        public List<string> BotIds { get; } = [];
        public List<BotLifecycleAction> Actions { get; } = [];
        public Queue<DockerLifecycleResult> Results { get; } = new();
        public bool Block;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DockerLifecycleResult> ExecuteAsync(
            ManagedBot bot,
            BotLifecycleAction action,
            CancellationToken cancellationToken = default)
        {
            BotIds.Add(bot.Id);
            Actions.Add(action);
            Started.TrySetResult();
            if (Block)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }

            return Results.Count == 0
                ? new DockerLifecycleResult("succeeded", "healthy")
                : Results.Dequeue();
        }
    }
}
