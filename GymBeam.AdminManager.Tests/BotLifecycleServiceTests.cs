using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Lifecycle;
using GymBeam.AdminManager.Registry;
using static GymBeam.AdminManager.Tests.DockerStatusReaderTestSupport;

namespace GymBeam.AdminManager.Tests;

public class BotLifecycleServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"manager-lifecycle-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExecuteAsync_DoubleClickAllowsOnlyOneOperationAndAuditsBothResults()
    {
        var controller = new BlockingLifecycleController();
        string auditPath = Path.Combine(_directory, "audit.jsonl");
        var service = new BotLifecycleService(
            new FakeRegistry([CreateBot("bot1")]),
            controller,
            new AuditLogger(auditPath, TimeProvider.System),
            TimeProvider.System,
            new BotOperationCoordinator());

        Task<BotLifecycleOperation> first = service.ExecuteAsync(
            "bot1", BotLifecycleAction.Restart, "admin", "127.0.0.1");
        await controller.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        BotLifecycleOperation second = await service.ExecuteAsync(
            "bot1", BotLifecycleAction.Restart, "admin", "127.0.0.1");
        controller.Release.SetResult();
        BotLifecycleOperation completed = await first;

        Assert.Equal("busy", second.Outcome);
        Assert.Equal("succeeded", completed.Outcome);
        Assert.Equal(1, controller.MaximumConcurrency);
        string audit = await File.ReadAllTextAsync(auditPath);
        Assert.Contains("bot.lifecycle.restart", audit);
        Assert.Contains("busy", audit);
        Assert.Contains("succeeded", audit);
        Assert.DoesNotContain("secret", audit, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../caddy")]
    [InlineData("gymbeam-admin-manager")]
    [InlineData("BOT1")]
    public async Task ExecuteAsync_InvalidOrUnregisteredBotNeverCallsDocker(string botId)
    {
        var controller = new BlockingLifecycleController();
        var service = new BotLifecycleService(
            new FakeRegistry([CreateBot("bot1")]),
            controller,
            new AuditLogger(Path.Combine(_directory, "audit.jsonl"), TimeProvider.System),
            TimeProvider.System,
            new BotOperationCoordinator());

        BotLifecycleOperation result = await service.ExecuteAsync(
            botId, BotLifecycleAction.Stop, "admin", "127.0.0.1");

        Assert.Equal("bot_not_found", result.Outcome);
        Assert.Equal(0, controller.CallCount);
        if (!BotIdValidatorForTest(botId))
        {
            string audit = await File.ReadAllTextAsync(Path.Combine(_directory, "audit.jsonl"));
            Assert.DoesNotContain(botId, audit, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DisabledOrDeletedBotNeverCallsDocker()
    {
        var controller = new BlockingLifecycleController();
        ManagedBot disabled = CreateBot("bot1") with
        {
            Enabled = false,
            LifecycleState = "disabled"
        };
        var service = new BotLifecycleService(
            new FakeRegistry([disabled]),
            controller,
            new AuditLogger(Path.Combine(_directory, "audit.jsonl"), TimeProvider.System),
            TimeProvider.System,
            new BotOperationCoordinator());

        BotLifecycleOperation result = await service.ExecuteAsync(
            "bot1", BotLifecycleAction.Start, "admin", "127.0.0.1");

        Assert.Equal("bot_disabled", result.Outcome);
        Assert.Equal(0, controller.CallCount);
    }

    private static bool BotIdValidatorForTest(string value) =>
        value.Length is >= 1 and <= 64
        && value[0] is >= 'a' and <= 'z'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

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

    private sealed class BlockingLifecycleController : IDockerLifecycleController
    {
        private int _concurrency;
        public int CallCount;
        public int MaximumConcurrency;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DockerLifecycleResult> ExecuteAsync(
            ManagedBot bot,
            BotLifecycleAction action,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            int concurrency = Interlocked.Increment(ref _concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, concurrency);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref _concurrency);
            return new DockerLifecycleResult("succeeded", "Operation completed");
        }
    }
}
