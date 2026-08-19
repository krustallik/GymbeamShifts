using System.Net;
using System.Text.Json;
using GymBeam.AdminManager.Provisioning;
using static GymBeam.AdminManager.Tests.DockerStatusReaderTestSupport;

namespace GymBeam.AdminManager.Tests;

public sealed class SafeDockerProvisionerTests
{
    [Fact]
    public async Task CreateAsync_UsesOnlyDerivedIdentityRequiredLabelsAndResourceLimits()
    {
        JsonDocument? payload = null;
        var handler = new RecordingHttpMessageHandler(async (request, _, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == "/containers/create")
            {
                payload = await JsonDocument.ParseAsync(
                    await request.Content!.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                return JsonResponse($"{{\"Id\":\"{Bot1ContainerId}\"}}", HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var provisioner = Create(handler);

        ResourceResult result = await provisioner.CreateAsync(Spec());

        Assert.True(result.Succeeded);
        Assert.Equal("/containers/create?name=gymbeam-shifts-bot3", handler.Requests.First().PathAndQuery);
        JsonElement root = payload!.RootElement;
        Assert.Equal("gymbeam-shifts-bot:latest", root.GetProperty("Image").GetString());
        Assert.Equal("true", root.GetProperty("Labels").GetProperty("com.gymbeam.managed").GetString());
        Assert.Equal("bot3", root.GetProperty("Labels").GetProperty("com.gymbeam.bot-id").GetString());
        Assert.Equal("bot", root.GetProperty("Labels").GetProperty("com.gymbeam.role").GetString());
        Assert.Equal("gymbeam-bot-bot3", root.GetProperty("Labels").GetProperty("com.docker.compose.service").GetString());
        Assert.Equal(805306368, root.GetProperty("HostConfig").GetProperty("Memory").GetInt64());
        Assert.Equal(1610612736, root.GetProperty("HostConfig").GetProperty("MemorySwap").GetInt64());
        Assert.Equal(268435456, root.GetProperty("HostConfig").GetProperty("ShmSize").GetInt64());
        Assert.Equal("gymbeam-internal", root.GetProperty("HostConfig").GetProperty("NetworkMode").GetString());
        JsonElement logConfig = root.GetProperty("HostConfig").GetProperty("LogConfig");
        Assert.Equal("json-file", logConfig.GetProperty("Type").GetString());
        Assert.Equal("10m", logConfig.GetProperty("Config").GetProperty("max-size").GetString());
        Assert.Equal("3", logConfig.GetProperty("Config").GetProperty("max-file").GetString());
        Assert.DoesNotContain("caddy", payload.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_AcceptsBoundedChunkedDockerCreateResponse()
    {
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/containers/create")
            {
                var response = JsonResponse($"{{\"Id\":\"{Bot1ContainerId}\"}}", HttpStatusCode.Created);
                response.Content.Headers.ContentLength = null;
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var provisioner = Create(handler);

        ResourceResult result = await provisioner.CreateAsync(Spec());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task CreateAsync_RejectsOversizedChunkedDockerResponseAsInvalidResponse()
    {
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/containers/create")
            {
                var response = new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new ByteArrayContent(new byte[1024 * 1024 + 1])
                };
                response.Content.Headers.ContentLength = null;
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var provisioner = Create(handler);

        ResourceResult result = await provisioner.CreateAsync(Spec());

        Assert.False(result.Succeeded);
        Assert.Equal("invalid_response", result.Outcome);
    }

    [Fact]
    public async Task RemoveAsync_SpoofedUnmanagedContainerFailsClosedAndNeverDeletes()
    {
        string list = "[{\"Id\":\"" + Bot1ContainerId
            + "\",\"Names\":[\"/gymbeam-shifts-bot3\"],\"Labels\":{\"com.gymbeam.managed\":\"false\","
            + "\"com.gymbeam.bot-id\":\"bot3\",\"com.gymbeam.role\":\"bot\","
            + "\"com.docker.compose.service\":\"gymbeam-bot-bot3\"}}]";
        var handler = new RecordingHttpMessageHandler((_, _, _) => Task.FromResult(JsonResponse(list)));
        var provisioner = Create(handler);

        ResourceResult result = await provisioner.RemoveAsync(Spec());

        Assert.False(result.Succeeded);
        Assert.Equal("identity_mismatch", result.Outcome);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task RemoveAsync_ForgedManagerOrCaddyIdentityNeverCallsDocker()
    {
        var handler = new RecordingHttpMessageHandler((_, _, _) => throw new InvalidOperationException());
        var provisioner = Create(handler);

        ResourceResult result = await provisioner.RemoveAsync(Spec() with
        {
            ContainerName = "gymbeam-admin-manager",
            ComposeServiceName = "caddy"
        });

        Assert.Equal("identity_mismatch", result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RemoveAsync_MissingTargetWithOtherManagedBotsIsIdempotentSuccess()
    {
        string other = ListEntry(Bot1ContainerId, "bot1", 1);
        var handler = new RecordingHttpMessageHandler((_, _, _) =>
            Task.FromResult(JsonResponse($"[{other}]")));
        var provisioner = Create(handler);

        ResourceResult result = await provisioner.RemoveAsync(Spec());

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    private static SafeDockerProvisioner Create(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("http://docker") },
        TimeSpan.FromSeconds(1),
        "gymbeam-shifts-bot:latest",
        "gymbeam-internal",
        "/opt/gymbeam/instances",
        selfContainerId: null);

    private static ProvisioningSpec Spec() => new(
        "bot3", "Bot Three", "bot3", "bot3.example.test", "gymbeam-bot-bot3",
        "gymbeam-shifts-bot3", "bot3");
}
