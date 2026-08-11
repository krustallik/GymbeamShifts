using System.Net;
using GymBeam.AdminManager.Provisioning;
using static GymBeam.AdminManager.Tests.DockerStatusReaderTestSupport;

namespace GymBeam.AdminManager.Tests;

public sealed class SafeCaddyRouteManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"caddy-routes-{Guid.NewGuid():N}");

    [Fact]
    public async Task AddAsync_WritesDerivedRouteAndValidatesBeforeLoad()
    {
        Directory.CreateDirectory(_root);
        string caddyfile = Path.Combine(_root, "Caddyfile");
        await File.WriteAllTextAsync(caddyfile, "import /etc/caddy/dynamic/*.caddy");
        string? loadedConfiguration = null;
        var handler = new RecordingHttpMessageHandler(async (request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/adapt")
            {
                return JsonResponse("{\"result\":{\"apps\":{}},\"warnings\":[\"test warning\"]}");
            }

            loadedConfiguration = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var manager = Create(caddyfile, handler);

        ResourceResult result = await manager.AddAsync(Spec());

        Assert.True(result.Succeeded);
        string route = await File.ReadAllTextAsync(Path.Combine(_root, "bot3.caddy"));
        Assert.Contains("bot3.example.test", route, StringComparison.Ordinal);
        Assert.Contains("reverse_proxy gymbeam-shifts-bot3:8080", route, StringComparison.Ordinal);
        Assert.Equal("{\"apps\":{}}", loadedConfiguration);
        Assert.Collection(handler.Requests,
            request => Assert.Equal((HttpMethod.Post, "/adapt"), request),
            request => Assert.Equal((HttpMethod.Post, "/load"), request));
    }

    [Fact]
    public async Task AddAsync_CaddyLoadFailureRemovesNewRouteAndAttemptsPreviousReload()
    {
        Directory.CreateDirectory(_root);
        string caddyfile = Path.Combine(_root, "Caddyfile");
        await File.WriteAllTextAsync(caddyfile, "import /etc/caddy/dynamic/*.caddy");
        int loadCount = 0;
        var handler = new RecordingHttpMessageHandler((request, _, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/adapt"
                ? JsonResponse("{\"result\":{\"apps\":{}}}")
                : Interlocked.Increment(ref loadCount) == 1
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                    : new HttpResponseMessage(HttpStatusCode.OK)));
        var manager = Create(caddyfile, handler);

        ResourceResult result = await manager.AddAsync(Spec());

        Assert.False(result.Succeeded);
        Assert.Equal("caddy_reload_failed", result.Outcome);
        Assert.False(File.Exists(Path.Combine(_root, "bot3.caddy")));
        Assert.Equal(2, loadCount);
    }

    [Theory]
    [InlineData("{\"apps\":{}}")]
    [InlineData("{\"result\":null}")]
    [InlineData("not-json")]
    public async Task AddAsync_InvalidAdaptResponseDoesNotCallLoad(string responseBody)
    {
        Directory.CreateDirectory(_root);
        string caddyfile = Path.Combine(_root, "Caddyfile");
        await File.WriteAllTextAsync(caddyfile, "import /etc/caddy/dynamic/*.caddy");
        var handler = new RecordingHttpMessageHandler((request, _, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/adapt"
                ? JsonResponse(responseBody)
                : throw new InvalidOperationException("Load must not be called")));
        var manager = Create(caddyfile, handler);

        ResourceResult result = await manager.AddAsync(Spec());

        Assert.False(result.Succeeded);
        Assert.Equal("caddy_invalid_response", result.Outcome);
        Assert.False(File.Exists(Path.Combine(_root, "bot3.caddy")));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("/adapt", request.PathAndQuery));
    }

    [Fact]
    public async Task AddAsync_ForgedHostOrIdentityCreatesNoFileAndCallsNoAdminApi()
    {
        Directory.CreateDirectory(_root);
        string caddyfile = Path.Combine(_root, "Caddyfile");
        await File.WriteAllTextAsync(caddyfile, "{}");
        var handler = new RecordingHttpMessageHandler((_, _, _) => throw new InvalidOperationException());
        var manager = Create(caddyfile, handler);

        ResourceResult result = await manager.AddAsync(Spec() with { PublicHost = "admin.example.test" });

        Assert.Equal("identity_mismatch", result.Outcome);
        Assert.Empty(handler.Requests);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.caddy"));
    }

    private SafeCaddyRouteManager Create(string caddyfile, HttpMessageHandler handler) => new(
        _root,
        caddyfile,
        "example.test",
        new HttpClient(handler) { BaseAddress = new Uri("http://caddy:2019") },
        TimeSpan.FromSeconds(1));

    private static ProvisioningSpec Spec() => new(
        "bot3", "Bot", "bot3", "bot3.example.test", "gymbeam-bot-bot3",
        "gymbeam-shifts-bot3", "bot3");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
