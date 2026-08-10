using System.Net;
using GymBeam.AdminManager.Docker;
using static GymBeam.AdminManager.Tests.DockerStatusReaderTestSupport;

namespace GymBeam.AdminManager.Tests;

public class SafeDockerStatusReaderResilienceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetStatusesAsync_OneInspectFailureDoesNotHideOtherContainer()
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)},{ListEntry(Bot2ContainerId, "bot2", 2)}]";
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/containers/json")
            {
                return Task.FromResult(JsonResponse(list));
            }

            return Task.FromResult(path.Contains(Bot1ContainerId, StringComparison.Ordinal)
                ? JsonResponse(InspectResponse(Bot1ContainerId, "bot1", 1))
                : JsonResponse("{}", HttpStatusCode.InternalServerError));
        });
        SafeDockerStatusReader reader = CreateReader(handler, TimeSpan.FromSeconds(2));

        IReadOnlyDictionary<string, BotRuntimeStatus> statuses = await reader.GetStatusesAsync(
            [CreateBot("bot1"), CreateBot("bot2")]);

        Assert.Equal("running", statuses["bot1"].State);
        Assert.Equal("unknown", statuses["bot2"].State);
    }

    [Fact]
    public async Task GetStatusesAsync_DockerUnavailableThenRecoveredDoesNotKeepStaleFailure()
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)}]";
        int listAttempts = 0;
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/containers/json"
                && Interlocked.Increment(ref listAttempts) == 1)
            {
                throw new HttpRequestException("Docker restarting");
            }

            return Task.FromResult(request.RequestUri.AbsolutePath == "/containers/json"
                ? JsonResponse(list)
                : JsonResponse(InspectResponse(Bot1ContainerId, "bot1", 1)));
        });
        SafeDockerStatusReader reader = CreateReader(handler, TimeSpan.FromSeconds(2));

        IReadOnlyDictionary<string, BotRuntimeStatus> unavailable = await reader.GetStatusesAsync([CreateBot("bot1")]);
        IReadOnlyDictionary<string, BotRuntimeStatus> recovered = await reader.GetStatusesAsync([CreateBot("bot1")]);

        Assert.Equal("unknown", unavailable["bot1"].State);
        Assert.Equal("running", recovered["bot1"].State);
    }

    [Fact]
    public async Task GetStatusesAsync_DockerTimeoutReturnsControlledUnknown()
    {
        var handler = new RecordingHttpMessageHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return JsonResponse("[]");
        });
        SafeDockerStatusReader reader = CreateReader(handler, TimeSpan.FromMilliseconds(50));

        IReadOnlyDictionary<string, BotRuntimeStatus> statuses = await reader.GetStatusesAsync([CreateBot("bot1")]);

        Assert.Equal("unknown", statuses["bot1"].State);
    }

    [Fact]
    public async Task GetStatusesAsync_ConcurrentDashboardReadsRemainIndependent()
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)}]";
        var handler = new RecordingHttpMessageHandler((request, _, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/containers/json"
                ? JsonResponse(list)
                : JsonResponse(InspectResponse(Bot1ContainerId, "bot1", 1))));
        SafeDockerStatusReader reader = CreateReader(handler, TimeSpan.FromSeconds(2));

        IReadOnlyDictionary<string, BotRuntimeStatus>[] results = await Task.WhenAll(
            Enumerable.Range(0, 40).Select(_ => reader.GetStatusesAsync([CreateBot("bot1")])));

        Assert.All(results, result => Assert.Equal("running", result["bot1"].State));
    }

    private static SafeDockerStatusReader CreateReader(HttpMessageHandler handler, TimeSpan timeout)
    {
        return new SafeDockerStatusReader(
            new HttpClient(handler) { BaseAddress = new Uri("http://docker") },
            new ManualTimeProvider(Now),
            timeout,
            selfContainerId: null);
    }
}
