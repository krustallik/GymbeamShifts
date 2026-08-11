using System.Text.Json;
using GymBeam.AdminManager.Auditing;

namespace GymBeam.AdminManager.Tests;

public class AuditLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"manager-audit-{Guid.NewGuid():N}");

    [Fact]
    public async Task ConcurrentWritesProduceValidSecretFreeJsonLines()
    {
        string path = Path.Combine(_directory, "audit.jsonl");
        var logger = new AuditLogger(path, new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)));

        await Task.WhenAll(Enumerable.Range(0, 40).Select(index => logger.WriteAsync(
            "auth.login",
            index % 2 == 0 ? "success" : "failure",
            actor: "manager-admin",
            target: null,
            remoteAddress: "127.0.0.1")));

        string[] lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(40, lines.Length);
        Assert.All(lines, line =>
        {
            using JsonDocument json = JsonDocument.Parse(line);
            Assert.Equal("auth.login", json.RootElement.GetProperty("action").GetString());
            Assert.False(json.RootElement.TryGetProperty("password", out _));
            Assert.False(json.RootElement.TryGetProperty("token", out _));
            Assert.False(json.RootElement.TryGetProperty("secret", out _));
        });
    }

    [Fact]
    public async Task WriteAsync_RotatesAuditWhenItReachesFiveMegabytes()
    {
        string path = Path.Combine(_directory, "audit.jsonl");
        Directory.CreateDirectory(_directory);
        using (FileStream stream = File.Create(path)) stream.SetLength(5L * 1024 * 1024);
        var logger = new AuditLogger(path, TimeProvider.System);

        await logger.WriteAsync("test.action", "success", "admin", null, "127.0.0.1");

        Assert.Equal(5L * 1024 * 1024, new FileInfo(Path.Combine(_directory, "audit.1.jsonl")).Length);
        Assert.Single(await File.ReadAllLinesAsync(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
