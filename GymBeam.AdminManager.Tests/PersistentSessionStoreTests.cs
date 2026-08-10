using GymBeam.AdminManager.Storage;

namespace GymBeam.AdminManager.Tests;

public class PersistentSessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"manager-sessions-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task AddAsync_PersistsHashedSessionIdAcrossStoreRestart()
    {
        string path = Path.Combine(_directory, "sessions.json");
        var firstStore = new PersistentSessionStore(path, _clock);

        await firstStore.AddAsync("raw-session-id", "manager-admin", _clock.GetUtcNow().AddHours(1));
        var restartedStore = new PersistentSessionStore(path, _clock);

        Assert.True(await restartedStore.IsActiveAsync("raw-session-id", "manager-admin"));
        Assert.DoesNotContain("raw-session-id", await File.ReadAllTextAsync(path));
        AssertOwnerOnlyOnUnix(path);
    }

    [Fact]
    public async Task RevokeAsync_PersistsRevocationAcrossStoreRestart()
    {
        string path = Path.Combine(_directory, "sessions.json");
        var firstStore = new PersistentSessionStore(path, _clock);
        await firstStore.AddAsync("session-to-revoke", "manager-admin", _clock.GetUtcNow().AddHours(1));

        await firstStore.RevokeAsync("session-to-revoke");
        var restartedStore = new PersistentSessionStore(path, _clock);

        Assert.False(await restartedStore.IsActiveAsync("session-to-revoke", "manager-admin"));
    }

    [Fact]
    public async Task ConcurrentAdds_DoNotLoseSessionsOrCorruptJson()
    {
        string path = Path.Combine(_directory, "sessions.json");
        var stores = Enumerable.Range(0, 4)
            .Select(_ => new PersistentSessionStore(path, _clock))
            .ToArray();

        await Task.WhenAll(Enumerable.Range(0, 40).Select(index =>
            stores[index % stores.Length].AddAsync(
                $"session-{index}",
                "manager-admin",
                _clock.GetUtcNow().AddHours(1))));

        var restartedStore = new PersistentSessionStore(path, _clock);
        bool[] active = await Task.WhenAll(Enumerable.Range(0, 40).Select(index =>
            restartedStore.IsActiveAsync($"session-{index}", "manager-admin")));
        Assert.All(active, Assert.True);
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
