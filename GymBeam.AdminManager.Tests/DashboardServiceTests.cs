using GymBeam.AdminManager.Dashboard;
using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Tests;

public class DashboardServiceTests
{
    [Fact]
    public async Task GetAllAsync_ReturnsEveryRegisteredBotAndUsesUnknownForMissingContainer()
    {
        ManagedBot[] bots =
        [
            CreateBot("bot1", "Bot One"),
            CreateBot("bot2", "Bot Two")
        ];
        var registry = new FakeRegistry(bots);
        var docker = new FakeDockerStatusReader(new Dictionary<string, BotRuntimeStatus>
        {
            ["bot1"] = new(
                "running",
                "healthy",
                TimeSpan.FromHours(2),
                new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
                "available")
        });
        var service = new DashboardService(registry, docker);

        IReadOnlyList<BotDashboardItem> items = await service.GetAllAsync();

        Assert.Equal(2, items.Count);
        BotDashboardItem known = Assert.Single(items, item => item.Id == "bot1");
        Assert.Equal("running", known.State);
        Assert.Equal("healthy", known.Health);
        Assert.Equal(TimeSpan.FromHours(2), known.Uptime);
        BotDashboardItem missing = Assert.Single(items, item => item.Id == "bot2");
        Assert.Equal("unknown", missing.State);
        Assert.Equal("unknown", missing.Health);
        Assert.Null(missing.Uptime);
        Assert.Null(missing.LastUpdatedAtUtc);
    }

    [Fact]
    public async Task GetAllAsync_PreservesKnownBotsWhenOneContainerInspectionFails()
    {
        ManagedBot[] bots =
        [
            CreateBot("bot1", "Bot One"),
            CreateBot("bot2", "Bot Two")
        ];
        var registry = new FakeRegistry(bots);
        var docker = new FakeDockerStatusReader(new Dictionary<string, BotRuntimeStatus>
        {
            ["bot1"] = new("running", "healthy", TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow, "available"),
            ["bot2"] = BotRuntimeStatus.Unknown("inspect-failed")
        });

        IReadOnlyList<BotDashboardItem> items = await new DashboardService(registry, docker).GetAllAsync();

        Assert.Equal("running", Assert.Single(items, item => item.Id == "bot1").State);
        Assert.Equal("unknown", Assert.Single(items, item => item.Id == "bot2").State);
    }

    private static ManagedBot CreateBot(string id, string displayName)
    {
        return new ManagedBot(
            id,
            displayName,
            $"gymbeam-{id}",
            $"gymbeam-shifts-{id}",
            Path.Combine(Path.GetTempPath(), id),
            Enabled: true,
            RegistrationSource: "test",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
    }

    private sealed class FakeRegistry(IReadOnlyList<ManagedBot> bots) : IManagedBotRegistry
    {
        public Task<IReadOnlyList<ManagedBot>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(bots);
        }
    }

    private sealed class FakeDockerStatusReader(
        IReadOnlyDictionary<string, BotRuntimeStatus> statuses) : IDockerStatusReader
    {
        public Task<IReadOnlyDictionary<string, BotRuntimeStatus>> GetStatusesAsync(
            IReadOnlyList<ManagedBot> bots,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(statuses);
        }
    }
}
