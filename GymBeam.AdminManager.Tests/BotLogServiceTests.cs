using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Logs;
using GymBeam.AdminManager.Registry;
using static GymBeam.AdminManager.Tests.DockerStatusReaderTestSupport;

namespace GymBeam.AdminManager.Tests;

public class BotLogServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"manager-logs-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("../caddy", 100)]
    [InlineData("bot1", 0)]
    [InlineData("bot1", 501)]
    public async Task ReadAsync_TraversalOrInvalidTailNeverCallsDocker(string botId, int tail)
    {
        var reader = new FakeLogReader();
        var service = new BotLogService(
            new FakeRegistry([CreateBot("bot1")]),
            reader,
            new AuditLogger(Path.Combine(_directory, "audit.jsonl"), TimeProvider.System));

        BotLogView result = await service.ReadAsync(botId, tail, "admin", "127.0.0.1");

        Assert.Equal("invalid_request", result.Outcome);
        Assert.Equal(0, reader.CallCount);
    }

    [Fact]
    public async Task ReadAsync_ReturnsTailFromPersistentApplicationLogAndRedactsSecrets()
    {
        string instancePath = Path.Combine(_directory, "bot1");
        string runtimePath = Path.Combine(instancePath, "runtime-data");
        Directory.CreateDirectory(runtimePath);
        await File.WriteAllLinesAsync(Path.Combine(runtimePath, "app.log"),
        [
            "old line",
            "password=very-secret",
            "latest line"
        ]);
        ManagedBot bot = CreateBot("bot1") with { InstancePath = instancePath };
        var service = new BotLogService(
            new FakeRegistry([bot]),
            new FakeLogReader(),
            new AuditLogger(Path.Combine(_directory, "audit.jsonl"), TimeProvider.System));

        BotLogView result = await service.ReadAsync("bot1", 2, "admin", "127.0.0.1");

        Assert.Equal("succeeded", result.Outcome);
        Assert.DoesNotContain("old line", result.Logs);
        Assert.DoesNotContain("very-secret", result.Logs);
        Assert.Contains("[REDACTED]", result.Logs);
        Assert.Contains("latest line", result.Logs);
        Assert.DoesNotContain("docker logs", result.Logs);
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

    private sealed class FakeLogReader : IDockerLogReader
    {
        public int CallCount;

        public Task<DockerLogResult> ReadAsync(
            ManagedBot bot,
            int tail,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(new DockerLogResult("succeeded", "docker logs"));
        }
    }
}
