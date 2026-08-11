using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Lifecycle;
using GymBeam.AdminManager.Provisioning;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Tests;

public class BotProvisioningServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"manager-provision-{Guid.NewGuid():N}");

    [Fact]
    public async Task ProvisionAsync_ExecutesOrderedTransactionAndActivatesRegistryLast()
    {
        var resources = new RecordingResources();
        var registry = new MutableRegistry();
        BotProvisioningService service = CreateService(resources, registry);

        ProvisioningResult result = await service.ProvisionAsync(
            ValidRequest(), "admin", "127.0.0.1");

        Assert.Equal("succeeded", result.Outcome);
        Assert.Equal(
            ["preflight", "files.create", "container.create", "route.add", "health.internal", "health.external"],
            resources.Calls);
        ManagedBot bot = Assert.Single(await registry.GetAllAsync());
        Assert.True(bot.Enabled);
        Assert.Equal("active", bot.LifecycleState);
        Assert.Equal("bot3.mapa-svietidiel.sk", bot.PublicHost);
        Assert.Equal(Path.Combine(Path.GetFullPath(_directory), "bot3"), bot.InstancePath);
        Assert.DoesNotContain("secret", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../bot3", "bot3")]
    [InlineData("BOT3", "bot3")]
    [InlineData("bot3", "../admin")]
    [InlineData("gymbeam-admin-manager", "manager")]
    [InlineData("caddy", "caddy")]
    public async Task ProvisionAsync_InvalidOrReservedInputCreatesNothing(string botId, string subdomain)
    {
        var resources = new RecordingResources();
        var registry = new MutableRegistry();
        BotProvisioningService service = CreateService(resources, registry);

        ProvisioningResult result = await service.ProvisionAsync(
            ValidRequest() with { BotId = botId, Subdomain = subdomain },
            "admin",
            "127.0.0.1");

        Assert.Equal("invalid_request", result.Outcome);
        Assert.Empty(resources.Calls);
        Assert.Empty(await registry.GetAllAsync());
    }

    [Theory]
    [InlineData("insufficient_disk")]
    [InlineData("insufficient_ram")]
    public async Task ProvisionAsync_InsufficientCapacityCreatesNothing(string outcome)
    {
        var resources = new RecordingResources { FailureAt = "preflight", FailureOutcome = outcome };
        BotProvisioningService service = CreateService(resources, new MutableRegistry());

        ProvisioningResult result = await service.ProvisionAsync(
            ValidRequest(), "admin", "127.0.0.1");

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(["preflight"], resources.Calls);
    }

    [Theory]
    [InlineData("files.create")]
    [InlineData("container.create")]
    [InlineData("route.add")]
    [InlineData("health.internal")]
    [InlineData("health.external")]
    public async Task ProvisionAsync_PartialFailureRollsBackAllDerivedResourcesInReverseOrder(string failureAt)
    {
        var resources = new RecordingResources { FailureAt = failureAt };
        var registry = new MutableRegistry();
        BotProvisioningService service = CreateService(resources, registry);

        ProvisioningResult result = await service.ProvisionAsync(
            ValidRequest(), "admin", "127.0.0.1");

        Assert.Equal("rolled_back", result.Outcome);
        Assert.Equal(
            ["route.remove", "container.remove", "files.remove"],
            resources.Calls.TakeLast(3));
        Assert.Empty(await registry.GetAllAsync());
    }

    [Fact]
    public async Task ProvisionAsync_RollbackFailureKeepsDurablePendingTransaction()
    {
        var resources = new RecordingResources
        {
            FailureAt = "route.add",
            RollbackFailureAt = "container.remove"
        };
        var transactions = new InMemoryProvisioningTransactions();
        BotProvisioningService service = CreateService(resources, new MutableRegistry(), transactions);

        ProvisioningResult result = await service.ProvisionAsync(
            ValidRequest(), "admin", "127.0.0.1");

        Assert.Equal("rollback_failed", result.Outcome);
        Assert.Single(await transactions.GetPendingAsync());
    }

    [Fact]
    public async Task ProvisionAsync_UnexpectedResourceExceptionStillAttemptsEveryRollbackStep()
    {
        var resources = new RecordingResources { ThrowAt = "container.create" };
        var transactions = new InMemoryProvisioningTransactions();
        BotProvisioningService service = CreateService(resources, new MutableRegistry(), transactions);

        ProvisioningResult result = await service.ProvisionAsync(
            ValidRequest(), "admin", "127.0.0.1");

        Assert.Equal("rolled_back", result.Outcome);
        Assert.Equal(
            ["route.remove", "container.remove", "files.remove"],
            resources.Calls.TakeLast(3));
        Assert.Empty(await transactions.GetPendingAsync());
    }

    [Fact]
    public async Task RecoverAsync_ManagerRestartRollsBackEveryPossibleResourceIdempotently()
    {
        var resources = new RecordingResources();
        var transactions = new InMemoryProvisioningTransactions();
        ProvisioningSpec spec = ProvisioningValidation.Validate(ValidRequest(), "mapa-svietidiel.sk");
        await transactions.BeginAsync(spec);
        var recovery = new ProvisioningRecoveryService(
            new MutableRegistry(),
            resources,
            transactions,
            new AuditLogger(Path.Combine(_directory, "audit.jsonl"), TimeProvider.System),
            new BotOperationCoordinator());

        await recovery.RecoverAsync();

        Assert.Equal(["route.remove", "container.remove", "files.remove"], resources.Calls);
        Assert.Empty(await transactions.GetPendingAsync());
    }

    [Fact]
    public async Task RecoverAsync_ActiveRegistryRecordCompletesJournalWithoutDeletingResources()
    {
        var resources = new RecordingResources();
        var transactions = new InMemoryProvisioningTransactions();
        var registry = new MutableRegistry();
        ProvisioningSpec spec = ProvisioningValidation.Validate(ValidRequest(), "mapa-svietidiel.sk");
        await transactions.BeginAsync(spec);
        await registry.AddActiveAsync(spec.ToManagedBot(TimeProvider.System.GetUtcNow(), _directory));
        var recovery = new ProvisioningRecoveryService(
            registry,
            resources,
            transactions,
            new AuditLogger(Path.Combine(_directory, "audit.jsonl"), TimeProvider.System),
            new BotOperationCoordinator());

        await recovery.RecoverAsync();

        Assert.Empty(resources.Calls);
        Assert.Empty(await transactions.GetPendingAsync());
    }

    [Fact]
    public async Task ProvisionAsync_RepeatedCompletedRequestIsPredictableAndDoesNotTouchResources()
    {
        var registry = new MutableRegistry();
        ProvisioningSpec spec = ProvisioningValidation.Validate(ValidRequest(), "mapa-svietidiel.sk");
        await registry.AddActiveAsync(spec.ToManagedBot(TimeProvider.System.GetUtcNow(), _directory));
        var resources = new RecordingResources();
        BotProvisioningService service = CreateService(resources, registry);

        ProvisioningResult result = await service.ProvisionAsync(
            ValidRequest(), "admin", "127.0.0.1");

        Assert.Equal("already_exists", result.Outcome);
        Assert.Empty(resources.Calls);
    }

    private BotProvisioningService CreateService(
        IProvisioningResources resources,
        IManagedBotRegistryMutations registry,
        IProvisioningTransactionStore? transactions = null) =>
        new(
            registry,
            resources,
            transactions ?? new InMemoryProvisioningTransactions(),
            new AuditLogger(Path.Combine(_directory, "audit.jsonl"), TimeProvider.System),
            new BotOperationCoordinator(),
            TimeProvider.System,
            "mapa-svietidiel.sk",
            _directory);

    private static ProvisionBotRequest ValidRequest() => new(
        "bot3",
        "Bot Three",
        "bot3",
        "login=user",
        "password with spaces",
        "123456:telegram-token-value-long",
        "-100123456789",
        "admin",
        "admin password",
        "admin token secret");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class RecordingResources : IProvisioningResources
    {
        public List<string> Calls { get; } = [];
        public string? FailureAt;
        public string FailureOutcome = "resource_failure";
        public string? RollbackFailureAt;
        public string? ThrowAt;

        public Task<ResourceResult> PreflightAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Call("preflight");
        public Task<ResourceResult> CreateFilesAsync(ProvisioningSpec spec, ProvisionBotRequest request, CancellationToken cancellationToken = default) => Call("files.create");
        public Task<ResourceResult> CreateContainerAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Call("container.create");
        public Task<ResourceResult> WaitInternalHealthAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Call("health.internal");
        public Task<ResourceResult> AddRouteAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Call("route.add");
        public Task<ResourceResult> WaitExternalHealthAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Call("health.external");
        public Task<ResourceResult> RemoveRouteAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Rollback("route.remove");
        public Task<ResourceResult> RemoveContainerAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Rollback("container.remove");
        public Task<ResourceResult> RemoveFilesAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Rollback("files.remove");

        private Task<ResourceResult> Call(string name)
        {
            Calls.Add(name);
            if (name == ThrowAt)
            {
                throw new IOException("simulated crash");
            }
            return Task.FromResult(name == FailureAt
                ? ResourceResult.Failure(FailureOutcome)
                : ResourceResult.Success());
        }

        private Task<ResourceResult> Rollback(string name)
        {
            Calls.Add(name);
            return Task.FromResult(name == RollbackFailureAt
                ? ResourceResult.Failure("rollback_failure")
                : ResourceResult.Success());
        }
    }

    private sealed class MutableRegistry : IManagedBotRegistryMutations
    {
        private readonly List<ManagedBot> _bots = [];
        public Task<IReadOnlyList<ManagedBot>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedBot>>(_bots.ToArray());
        public Task AddActiveAsync(ManagedBot bot, CancellationToken cancellationToken = default)
        {
            _bots.Add(bot);
            return Task.CompletedTask;
        }
        public Task UpdateAsync(ManagedBot bot, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveAsync(string botId, CancellationToken cancellationToken = default)
        {
            _bots.RemoveAll(bot => string.Equals(bot.Id, botId, StringComparison.Ordinal));
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryProvisioningTransactions : IProvisioningTransactionStore
    {
        private readonly Dictionary<string, ProvisioningSpec> _pending = new(StringComparer.Ordinal);
        public Task<bool> BeginAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) =>
            Task.FromResult(_pending.TryAdd(spec.BotId, spec));
        public Task CompleteAsync(string botId, CancellationToken cancellationToken = default)
        {
            _pending.Remove(botId);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ProvisioningSpec>> GetPendingAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProvisioningSpec>>(_pending.Values.ToArray());
    }
}
