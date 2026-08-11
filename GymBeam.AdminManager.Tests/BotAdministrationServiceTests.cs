using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Lifecycle;
using GymBeam.AdminManager.Provisioning;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Tests;

public sealed class BotAdministrationServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"admin-lifecycle-{Guid.NewGuid():N}");

    [Fact]
    public async Task DeleteAsync_RequiresExactExplicitConfirmationBeforeMutation()
    {
        var context = Create();

        BotAdministrationResult result = await context.Service.DeleteAsync(
            "bot3", "delete bot3", "admin", "127.0.0.1");

        Assert.Equal("confirmation_required", result.Outcome);
        Assert.Empty(context.Calls);
        Assert.Equal("active", (await context.Registry.GetAllAsync()).Single().LifecycleState);
    }

    [Fact]
    public async Task DeleteAsync_RemovesEveryResourceAndRegistryEntryWithoutBackup()
    {
        var context = Create();

        BotAdministrationResult result = await context.Service.DeleteAsync(
            "bot3", "DELETE bot3", "admin", "127.0.0.1");

        Assert.Equal("succeeded", result.Outcome);
        Assert.Equal(["route.remove", "container.remove", "files.remove", "residual.remove"], context.Calls);
        Assert.Empty(await context.Registry.GetAllAsync());

        BotAdministrationResult repeated = await context.Service.DeleteAsync(
            "bot3", "DELETE bot3", "admin", "127.0.0.1");
        Assert.Equal("bot_not_found", repeated.Outcome);
        Assert.Equal(4, context.Calls.Count);
    }

    [Fact]
    public async Task DeleteAsync_ResourceFailureKeepsRetryableRegistryEntry()
    {
        var context = Create();
        context.Resources.FailureCall = "container.remove";

        BotAdministrationResult result = await context.Service.DeleteAsync(
            "bot3", "DELETE bot3", "admin", "127.0.0.1");

        Assert.Equal("resource_failed", result.Outcome);
        Assert.Equal(["route.remove", "container.remove"], context.Calls);
        ManagedBot failed = Assert.Single(await context.Registry.GetAllAsync());
        Assert.False(failed.Enabled);
        Assert.Equal("delete_failed", failed.LifecycleState);
    }

    [Fact]
    public async Task DisableAsync_StopFailureRestoresRouteAndKeepsRegistryActive()
    {
        var context = Create();
        context.Lifecycle.Outcome = "docker_unavailable";

        BotAdministrationResult result = await context.Service.DisableAsync(
            "bot3", "admin", "127.0.0.1");

        Assert.Equal("docker_unavailable", result.Outcome);
        Assert.Equal(["route.remove", "lifecycle.stop", "route.add"], context.Calls);
        Assert.True((await context.Registry.GetAllAsync()).Single().Enabled);
    }

    [Fact]
    public async Task EnableAsync_StartHealthRouteExternalThenActivatesRegistry()
    {
        var context = Create(enabled: false);

        BotAdministrationResult result = await context.Service.EnableAsync(
            "bot3", "admin", "127.0.0.1");

        Assert.Equal("succeeded", result.Outcome);
        Assert.Equal(["lifecycle.start", "route.add", "health.external"], context.Calls);
        ManagedBot active = Assert.Single(await context.Registry.GetAllAsync());
        Assert.True(active.Enabled);
        Assert.Equal("active", active.LifecycleState);
        Assert.Equal("enabling", context.Registry.History[0].LifecycleState);
        Assert.False(context.Registry.History[0].Enabled);
    }

    [Fact]
    public async Task DisableAsync_PersistsSafeTransitionalStateBeforeExternalSideEffects()
    {
        var context = Create();

        BotAdministrationResult result = await context.Service.DisableAsync(
            "bot3", "admin", "127.0.0.1");

        Assert.Equal("succeeded", result.Outcome);
        Assert.Equal("disabling", context.Registry.History[0].LifecycleState);
        Assert.False(context.Registry.History[0].Enabled);
        Assert.Equal("disabled", (await context.Registry.GetAllAsync()).Single().LifecycleState);
    }

    [Theory]
    [InlineData("enabling")]
    [InlineData("disabling")]
    public async Task Recovery_FailsClosedIncompleteEnableOrDisableToDisabled(string state)
    {
        var context = Create(enabled: false, lifecycleState: state);
        var recovery = new BotAdministrationRecoveryService(context.Registry, context.Service);

        await recovery.RecoverAsync();

        ManagedBot bot = Assert.Single(await context.Registry.GetAllAsync());
        Assert.False(bot.Enabled);
        Assert.Equal("disabled", bot.LifecycleState);
        Assert.Contains("route.remove", context.Calls);
        Assert.Contains("lifecycle.stop", context.Calls);
    }

    [Fact]
    public async Task Recovery_ResumesIncompleteDeleteAndRemovesRegistryEntry()
    {
        var context = Create(enabled: false, lifecycleState: "deleting");
        var recovery = new BotAdministrationRecoveryService(context.Registry, context.Service);

        await recovery.RecoverAsync();

        Assert.Equal(["route.remove", "container.remove", "files.remove", "residual.remove"], context.Calls);
        Assert.Empty(await context.Registry.GetAllAsync());
    }

    private Context Create(bool enabled = true, string? lifecycleState = null)
    {
        var calls = new List<string>();
        var registry = new MutableRegistry(Bot(enabled) with
        {
            LifecycleState = lifecycleState ?? (enabled ? "active" : "disabled")
        });
        var resources = new Resources(calls);
        var lifecycle = new Lifecycle(calls);
        var residualData = new ResidualData(calls);
        var service = new BotAdministrationService(
            registry,
            resources,
            lifecycle,
            residualData,
            new BotOperationCoordinator(),
            new AuditLogger(Path.Combine(_directory, "audit.jsonl"), TimeProvider.System),
            TimeProvider.System,
            "example.test");
        return new Context(service, registry, resources, lifecycle, calls);
    }

    private static ManagedBot Bot(bool enabled) => new(
        "bot3", "Bot Three", "gymbeam-bot-bot3", "gymbeam-shifts-bot3", "bot3",
        enabled, "provision", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
    {
        LifecycleState = enabled ? "active" : "disabled",
        PublicHost = "bot3.example.test"
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed record Context(
        BotAdministrationService Service,
        MutableRegistry Registry,
        Resources Resources,
        Lifecycle Lifecycle,
        List<string> Calls);

    private sealed class MutableRegistry(ManagedBot bot) : IManagedBotRegistryMutations
    {
        private readonly List<ManagedBot> _bots = [bot];
        public List<ManagedBot> History { get; } = [];
        public Task<IReadOnlyList<ManagedBot>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ManagedBot>>(_bots.ToArray());
        public Task AddActiveAsync(ManagedBot value, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAsync(ManagedBot value, CancellationToken cancellationToken = default)
        {
            int index = _bots.FindIndex(bot => bot.Id == value.Id);
            if (index < 0) throw new KeyNotFoundException();
            _bots[index] = value;
            History.Add(value);
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string botId, CancellationToken cancellationToken = default)
        {
            _bots.RemoveAll(bot => bot.Id == botId);
            return Task.CompletedTask;
        }
    }

    private sealed class Resources(List<string> calls) : IProvisioningResources
    {
        public string? FailureCall;
        public Task<ResourceResult> PreflightAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Success();
        public Task<ResourceResult> CreateFilesAsync(ProvisioningSpec spec, ProvisionBotRequest request, CancellationToken cancellationToken = default) => Success();
        public Task<ResourceResult> CreateContainerAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Success();
        public Task<ResourceResult> WaitInternalHealthAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Success();
        public Task<ResourceResult> AddRouteAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Call("route.add");
        public Task<ResourceResult> WaitExternalHealthAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Call("health.external");
        public Task<ResourceResult> RemoveRouteAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Call("route.remove");
        public Task<ResourceResult> RemoveContainerAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Call("container.remove");
        public Task<ResourceResult> RemoveFilesAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default) => Call("files.remove");
        private Task<ResourceResult> Call(string call)
        {
            calls.Add(call);
            return Task.FromResult(call == FailureCall
                ? ResourceResult.Failure("resource_failed")
                : ResourceResult.Success());
        }
        private static Task<ResourceResult> Success() => Task.FromResult(ResourceResult.Success());
    }

    private sealed class Lifecycle(List<string> calls) : IDockerLifecycleController
    {
        public string Outcome = "succeeded";
        public Task<DockerLifecycleResult> ExecuteAsync(ManagedBot bot, BotLifecycleAction action, CancellationToken cancellationToken = default)
        {
            calls.Add($"lifecycle.{action.ToString().ToLowerInvariant()}");
            return Task.FromResult(new DockerLifecycleResult(Outcome, Outcome));
        }
    }

    private sealed class ResidualData(List<string> calls) : IBotResidualDataCleaner
    {
        public Task<ResourceResult> RemoveAsync(string botId, CancellationToken cancellationToken = default)
        {
            calls.Add("residual.remove");
            return Task.FromResult(ResourceResult.Success());
        }
    }

}
