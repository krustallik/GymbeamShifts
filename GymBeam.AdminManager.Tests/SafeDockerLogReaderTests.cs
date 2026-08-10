using System.Buffers.Binary;
using System.Net;
using System.Text;
using GymBeam.AdminManager.Docker;
using static GymBeam.AdminManager.Tests.DockerStatusReaderTestSupport;

namespace GymBeam.AdminManager.Tests;

public class SafeDockerLogReaderTests
{
    [Fact]
    public async Task ReadAsync_UsesValidatedIdFixedTailAndRedactsSecrets()
    {
        string logs = "password=hunter2 token=abc123 chat_id=-100123456 username=admin\n"
            + "telegram 123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef\n"
            + "Authorization: Bearer very-secret-token\n"
            + "channel -100987654321 JWT eyJhbGciOiJIUzI1NiJ9.cGF5bG9hZA.c2lnbmF0dXJl\nnormal line";
        var handler = CreateSuccessfulHandler(Encoding.UTF8.GetBytes(logs));
        var reader = CreateReader(handler);

        DockerLogResult result = await reader.ReadAsync(CreateBot("bot1"), 200);

        Assert.Equal("succeeded", result.Outcome);
        Assert.Contains("normal line", result.Logs);
        Assert.DoesNotContain("hunter2", result.Logs);
        Assert.DoesNotContain("abc123", result.Logs);
        Assert.DoesNotContain("-100123456", result.Logs);
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZ", result.Logs);
        Assert.DoesNotContain("very-secret-token", result.Logs);
        Assert.DoesNotContain("-100987654321", result.Logs);
        Assert.DoesNotContain("eyJhbGci", result.Logs);
        Assert.Contains("[REDACTED]", result.Logs);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal("/containers/json", new Uri("http://docker" + request.PathAndQuery).AbsolutePath),
            request => Assert.Equal($"/containers/{Bot1ContainerId}/json", request.PathAndQuery),
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal(
                    $"/containers/{Bot1ContainerId}/logs?stdout=true&stderr=true&tail=200&timestamps=true",
                    request.PathAndQuery);
            });
    }

    [Fact]
    public async Task ReadAsync_ParsesDockerMultiplexedFrames()
    {
        byte[] first = Frame(1, Encoding.UTF8.GetBytes("stdout line\n"));
        byte[] second = Frame(2, Encoding.UTF8.GetBytes("stderr line\n"));
        var handler = CreateSuccessfulHandler([.. first, .. second]);
        var reader = CreateReader(handler);

        DockerLogResult result = await reader.ReadAsync(CreateBot("bot1"), 50);

        Assert.Contains("stdout line", result.Logs);
        Assert.Contains("stderr line", result.Logs);
    }

    [Fact]
    public async Task ReadAsync_RedactsQuotedCredentialsWithSpacesAndJsonRemainder()
    {
        const string secret = "password=\"multi word secret\" harmless=value\n"
            + "{\"chat_id\":\"-100987654\",\"message\":\"public\"}";
        var handler = CreateSuccessfulHandler(Encoding.UTF8.GetBytes(secret));
        var reader = CreateReader(handler);

        DockerLogResult result = await reader.ReadAsync(CreateBot("bot1"), 20);

        Assert.DoesNotContain("multi word secret", result.Logs);
        Assert.DoesNotContain("word secret", result.Logs);
        Assert.DoesNotContain("-100987654", result.Logs);
        Assert.Contains("[REDACTED]", result.Logs);
    }

    [Fact]
    public async Task ReadAsync_InvalidUtf8IsReplacedWithoutFailure()
    {
        var handler = CreateSuccessfulHandler([0x66, 0x6f, 0x80, 0x6f]);
        var reader = CreateReader(handler);

        DockerLogResult result = await reader.ReadAsync(CreateBot("bot1"), 20);

        Assert.Equal("succeeded", result.Outcome);
        Assert.Contains('\uFFFD', result.Logs);
    }

    [Fact]
    public async Task ReadAsync_ResponseOverByteLimitFailsClosed()
    {
        var handler = CreateSuccessfulHandler(Enumerable.Repeat((byte)'x', 2049).ToArray());
        var reader = CreateReader(handler, maximumResponseBytes: 2048);

        DockerLogResult result = await reader.ReadAsync(CreateBot("bot1"), 20);

        Assert.Equal("response_too_large", result.Outcome);
        Assert.Empty(result.Logs);
    }

    [Fact]
    public async Task ReadAsync_LogRotationDoesNotReturnCachedContent()
    {
        int logRequest = 0;
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)}]";
        var handler = new RecordingHttpMessageHandler((request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/containers/json")
            {
                return Task.FromResult(JsonResponse(list));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/json", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(InspectResponse(Bot1ContainerId, "bot1", 1)));
            }

            string value = Interlocked.Increment(ref logRequest) == 1 ? "before rotation" : "after rotation";
            return Task.FromResult(BytesResponse(Encoding.UTF8.GetBytes(value)));
        });
        var reader = CreateReader(handler);

        DockerLogResult before = await reader.ReadAsync(CreateBot("bot1"), 20);
        DockerLogResult after = await reader.ReadAsync(CreateBot("bot1"), 20);

        Assert.Equal("before rotation", before.Logs);
        Assert.Equal("after rotation", after.Logs);
    }

    [Fact]
    public async Task ReadAsync_IdentitySpoofingNeverRequestsLogs()
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)}]";
        var handler = new RecordingHttpMessageHandler((request, _, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/containers/json"
                ? JsonResponse(list)
                : JsonResponse(InspectResponse(Bot1ContainerId, "bot2", 1))));
        var reader = CreateReader(handler);

        DockerLogResult result = await reader.ReadAsync(CreateBot("bot1"), 20);

        Assert.Equal("identity_mismatch", result.Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static RecordingHttpMessageHandler CreateSuccessfulHandler(byte[] logs)
    {
        string list = $"[{ListEntry(Bot1ContainerId, "bot1", 1)}]";
        return new RecordingHttpMessageHandler((request, _, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath switch
            {
                "/containers/json" => JsonResponse(list),
                var path when path.EndsWith("/json", StringComparison.Ordinal) =>
                    JsonResponse(InspectResponse(Bot1ContainerId, "bot1", 1)),
                _ => BytesResponse(logs)
            }));
    }

    private static SafeDockerLogReader CreateReader(
        HttpMessageHandler handler,
        int maximumResponseBytes = 256 * 1024) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://docker") },
            TimeSpan.FromSeconds(1),
            maximumResponseBytes,
            selfContainerId: null);

    private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
    };

    private static byte[] Frame(byte stream, byte[] payload)
    {
        byte[] frame = new byte[8 + payload.Length];
        frame[0] = stream;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4, 4), payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }
}
