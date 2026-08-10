using System.Net;
using GymBeam.AdminManager.Docker;
using static GymBeam.AdminManager.Tests.DockerStatusReaderTestSupport;

namespace GymBeam.AdminManager.Tests;

public class SafeDockerLifecycleControllerTests
{
    [Fact]
    public async Task ExecuteAsync_RevalidatesIdentityThenStartsByContainerIdAndWaitsForHealth()
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)}]";
        int inspectCount = 0;
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/containers/json")
            {
                return Task.FromResult(JsonResponse(list));
            }

            if (path.EndsWith("/json", StringComparison.Ordinal))
            {
                int current = Interlocked.Increment(ref inspectCount);
                return Task.FromResult(JsonResponse(InspectResponse(
                    Bot1ContainerId,
                    "bot1",
                    1,
                    current == 1 ? "exited" : "running",
                    current == 1 ? null : "healthy")));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var controller = CreateController(handler);

        DockerLifecycleResult result = await controller.ExecuteAsync(
            CreateBot("bot1"),
            BotLifecycleAction.Start);

        Assert.Equal("succeeded", result.Outcome);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal("/containers/json?all=true&filters=%7B%22label%22%3A%5B%22com.gymbeam.managed%3Dtrue%22%5D%7D", request.PathAndQuery),
            request => Assert.Equal($"/containers/{Bot1ContainerId}/json", request.PathAndQuery),
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal($"/containers/{Bot1ContainerId}/start", request.PathAndQuery);
            },
            request => Assert.Equal($"/containers/{Bot1ContainerId}/json", request.PathAndQuery));
    }

    [Fact]
    public async Task ExecuteAsync_ChangedLabelsBeforeOperationFailsClosed()
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)}]";
        var handler = new RecordingHttpMessageHandler((request, _, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/containers/json"
                ? JsonResponse(list)
                : JsonResponse(InspectResponse(Bot1ContainerId, "bot2", 1, "exited", null))));
        var controller = CreateController(handler);

        DockerLifecycleResult result = await controller.ExecuteAsync(
            CreateBot("bot1"),
            BotLifecycleAction.Start);

        Assert.Equal("identity_mismatch", result.Outcome);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ExecuteAsync_RepeatedStartIsPredictableNoOp()
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)}]";
        var handler = new RecordingHttpMessageHandler((request, _, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/containers/json"
                ? JsonResponse(list)
                : JsonResponse(InspectResponse(Bot1ContainerId, "bot1", 1, "running", "healthy"))));
        var controller = CreateController(handler);

        DockerLifecycleResult result = await controller.ExecuteAsync(
            CreateBot("bot1"),
            BotLifecycleAction.Start);

        Assert.Equal("already_running", result.Outcome);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ExecuteAsync_ContainerDisappearingDuringOperationReturnsControlledResult()
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)}]";
        var handler = new RecordingHttpMessageHandler((request, _, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath switch
            {
                "/containers/json" => JsonResponse(list),
                var path when path.EndsWith("/json", StringComparison.Ordinal) =>
                    JsonResponse(InspectResponse(Bot1ContainerId, "bot1", 1, "exited", null)),
                _ => JsonResponse("{}", HttpStatusCode.NotFound)
            }));
        var controller = CreateController(handler);

        DockerLifecycleResult result = await controller.ExecuteAsync(
            CreateBot("bot1"),
            BotLifecycleAction.Start);

        Assert.Equal("container_not_found", result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_HealthTimeoutIsControlled()
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)}]";
        int inspectCount = 0;
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/containers/json")
            {
                return Task.FromResult(JsonResponse(list));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/json", StringComparison.Ordinal))
            {
                bool beforeStart = Interlocked.Increment(ref inspectCount) == 1;
                return Task.FromResult(JsonResponse(InspectResponse(
                    Bot1ContainerId,
                    "bot1",
                    1,
                    beforeStart ? "exited" : "running",
                    beforeStart ? null : "starting")));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var controller = CreateController(
            handler,
            healthTimeout: TimeSpan.FromMilliseconds(30),
            healthPollInterval: TimeSpan.FromMilliseconds(5));

        DockerLifecycleResult result = await controller.ExecuteAsync(
            CreateBot("bot1"),
            BotLifecycleAction.Start);

        Assert.Equal("health_timeout", result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_DockerUnavailableReturnsControlledResult()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            throw new HttpRequestException("socket unavailable secret-detail"));
        var controller = CreateController(handler);

        DockerLifecycleResult result = await controller.ExecuteAsync(
            CreateBot("bot1"),
            BotLifecycleAction.Restart);

        Assert.Equal("docker_unavailable", result.Outcome);
        Assert.DoesNotContain("secret-detail", result.Message);
    }

    private static SafeDockerLifecycleController CreateController(
        HttpMessageHandler handler,
        TimeSpan? healthTimeout = null,
        TimeSpan? healthPollInterval = null)
    {
        return new SafeDockerLifecycleController(
            new HttpClient(handler) { BaseAddress = new Uri("http://docker") },
            TimeSpan.FromSeconds(1),
            healthTimeout ?? TimeSpan.FromSeconds(1),
            healthPollInterval ?? TimeSpan.FromMilliseconds(1),
            selfContainerId: null);
    }
}
