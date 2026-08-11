using System.Collections.Concurrent;
using System.Net;
using System.Text;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Tests;

internal static class DockerStatusReaderTestSupport
{
    public const string Bot1ContainerId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string Bot2ContainerId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    public static ManagedBot CreateBot(string id)
    {
        int number = id == "bot1" ? 1 : 2;
        return new ManagedBot(
            id,
            $"Bot {number}",
            $"gymbeam-bot-{number}",
            $"gymbeam-shifts-bot-{number}",
            Path.Combine(Path.GetTempPath(), id),
            Enabled: true,
            RegistrationSource: "test",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
    }

    public static string ListEntry(
        string containerId,
        string botId,
        int number,
        string managed = "true",
        string role = "bot",
        string? composeService = null,
        string? containerName = null)
    {
        composeService ??= $"gymbeam-bot-{number}";
        containerName ??= $"gymbeam-shifts-bot-{number}";
        return $$"""
        {
          "Id": "{{containerId}}",
          "Names": ["/{{containerName}}"],
          "Labels": {
            "com.gymbeam.managed": "{{managed}}",
            "com.gymbeam.bot-id": "{{botId}}",
            "com.gymbeam.role": "{{role}}",
            "com.docker.compose.service": "{{composeService}}"
          }
        }
        """;
    }

    public static string InspectResponse(
        string containerId,
        string botId,
        int number,
        string state = "running",
        string? health = "healthy")
    {
        string healthJson = health is null
            ? string.Empty
            : $", \"Health\": {{ \"Status\": \"{health}\" }}";
        return $$"""
        {
          "Id": "{{containerId}}",
          "Name": "/gymbeam-shifts-bot-{{number}}",
          "Created": "2026-08-10T10:00:00Z",
          "Config": {
            "Labels": {
              "com.gymbeam.managed": "true",
              "com.gymbeam.bot-id": "{{botId}}",
              "com.gymbeam.role": "bot",
              "com.docker.compose.service": "gymbeam-bot-{{number}}"
            }
          },
          "State": {
            "Status": "{{state}}",
            "Running": {{(state == "running" ? "true" : "false")}},
            "StartedAt": "2026-08-10T12:00:00Z",
            "FinishedAt": "2026-08-10T13:00:00Z"{{healthJson}}
          }
        }
        """;
    }

    public static string StatsResponse(long usage, long inactiveFile = 0) => $$"""
        {
          "memory_stats": {
            "usage": {{usage}},
            "stats": { "inactive_file": {{inactiveFile}} }
          }
        }
        """;

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }
}

internal sealed class RecordingHttpMessageHandler(
    Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> responder)
    : HttpMessageHandler
{
    private int _requestCount;
    public ConcurrentQueue<(HttpMethod Method, string PathAndQuery)> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        int requestNumber = Interlocked.Increment(ref _requestCount);
        Requests.Enqueue((request.Method, request.RequestUri!.PathAndQuery));
        return responder(request, requestNumber, cancellationToken);
    }
}
