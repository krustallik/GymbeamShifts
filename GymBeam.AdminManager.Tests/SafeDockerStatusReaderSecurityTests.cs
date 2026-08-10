using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Registry;
using static GymBeam.AdminManager.Tests.DockerStatusReaderTestSupport;

namespace GymBeam.AdminManager.Tests;

public class SafeDockerStatusReaderSecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetStatusesAsync_UsesStaticLabelFilterAndInspectsOnlyValidatedContainerId()
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)}]";
        var handler = new RecordingHttpMessageHandler((request, _, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/containers/json"
                ? JsonResponse(list)
                : JsonResponse(InspectResponse(Bot1ContainerId, "bot1", 1))));
        var reader = CreateReader(handler);

        IReadOnlyDictionary<string, BotRuntimeStatus> statuses = await reader.GetStatusesAsync([CreateBot("bot1")]);

        BotRuntimeStatus status = statuses["bot1"];
        Assert.Equal("running", status.State);
        Assert.Equal("healthy", status.Health);
        Assert.Equal(TimeSpan.FromHours(2), status.Uptime);
        Assert.Equal(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero), status.LastUpdatedAtUtc);
        Assert.Collection(
            handler.Requests,
            listRequest =>
            {
                Assert.Equal(HttpMethod.Get, listRequest.Method);
                Assert.StartsWith("/containers/json?all=true&filters=", listRequest.PathAndQuery);
                string decoded = Uri.UnescapeDataString(listRequest.PathAndQuery);
                Assert.Contains("com.gymbeam.managed=true", decoded);
                Assert.DoesNotContain("gymbeam-shifts-bot-1", decoded);
            },
            inspectRequest =>
            {
                Assert.Equal(HttpMethod.Get, inspectRequest.Method);
                Assert.Equal($"/containers/{Bot1ContainerId}/json", inspectRequest.PathAndQuery);
                Assert.DoesNotContain("gymbeam-shifts-bot-1", inspectRequest.PathAndQuery);
            });
    }

    [Theory]
    [InlineData("false", "bot", "gymbeam-bot-1", "gymbeam-shifts-bot-1")]
    [InlineData("TRUE", "bot", "gymbeam-bot-1", "gymbeam-shifts-bot-1")]
    [InlineData("true", "service", "gymbeam-bot-1", "gymbeam-shifts-bot-1")]
    [InlineData("true", "bot", "other-service", "gymbeam-shifts-bot-1")]
    [InlineData("true", "bot", "gymbeam-bot-1", "third-party")]
    [InlineData("true", "bot", "caddy", "gymbeam-caddy")]
    [InlineData("true", "bot", "gymbeam-admin-manager", "gymbeam-admin-manager")]
    public async Task GetStatusesAsync_LabelOrIdentitySpoofingNeverTriggersInspect(
        string managed,
        string role,
        string composeService,
        string containerName)
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1, managed, role, composeService, containerName)}]";
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse(list)));
        var reader = CreateReader(handler);

        IReadOnlyDictionary<string, BotRuntimeStatus> statuses = await reader.GetStatusesAsync([CreateBot("bot1")]);

        Assert.Equal("unknown", statuses["bot1"].State);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("../caddy")]
    [InlineData("BOT1")]
    [InlineData("bot_1")]
    [InlineData("")]
    public async Task GetStatusesAsync_InvalidRegistryBotIdMakesNoDockerRequest(string botId)
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new InvalidOperationException("Docker must not be contacted"));
        var reader = CreateReader(handler);

        IReadOnlyDictionary<string, BotRuntimeStatus> statuses = await reader.GetStatusesAsync([CreateBot(botId)]);

        Assert.Equal("unknown", statuses[botId].State);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetStatusesAsync_MaliciousContainerIdCannotBecomeDockerPath()
    {
        string list = $"[{ListEntry("../../containers/caddy", "bot1", 1)}]";
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse(list)));
        var reader = CreateReader(handler);

        IReadOnlyDictionary<string, BotRuntimeStatus> statuses = await reader.GetStatusesAsync([CreateBot("bot1")]);

        Assert.Equal("unknown", statuses["bot1"].State);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetStatusesAsync_DuplicateManagedBotIdFailsClosedWithoutInspect()
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)},{ListEntry(Bot2ContainerId, "bot1", 1)}]";
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse(list)));
        var reader = CreateReader(handler);

        IReadOnlyDictionary<string, BotRuntimeStatus> statuses = await reader.GetStatusesAsync([CreateBot("bot1")]);

        Assert.Equal("unknown", statuses["bot1"].State);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetStatusesAsync_NeverInspectsOwnContainerEvenWithPerfectSpoofedLabels()
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)}]";
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse(list)));
        var reader = CreateReader(handler, selfContainerId: Bot1ContainerId[..12]);

        IReadOnlyDictionary<string, BotRuntimeStatus> statuses = await reader.GetStatusesAsync([CreateBot("bot1")]);

        Assert.Equal("unknown", statuses["bot1"].State);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetStatusesAsync_IdentityChangedBetweenListAndInspectFailsClosed()
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)}]";
        var handler = new RecordingHttpMessageHandler((request, _, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/containers/json"
                ? JsonResponse(list)
                : JsonResponse(InspectResponse(Bot1ContainerId, "bot2", 1))));
        var reader = CreateReader(handler);

        IReadOnlyDictionary<string, BotRuntimeStatus> statuses = await reader.GetStatusesAsync([CreateBot("bot1")]);

        Assert.Equal("unknown", statuses["bot1"].State);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static SafeDockerStatusReader CreateReader(
        HttpMessageHandler handler,
        string? selfContainerId = null)
    {
        return new SafeDockerStatusReader(
            new HttpClient(handler) { BaseAddress = new Uri("http://docker") },
            new ManualTimeProvider(Now),
            TimeSpan.FromSeconds(2),
            selfContainerId);
    }
}
