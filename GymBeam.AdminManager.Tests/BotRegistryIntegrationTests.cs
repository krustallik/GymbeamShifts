using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using GymBeam.AdminManager.Configuration;
using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Registry;
using GymBeam.AdminManager.Security;
using GymBeam.AdminManager.Lifecycle;
using GymBeam.AdminManager.Logs;
using GymBeam.AdminManager.Credentials;
using Microsoft.AspNetCore.Builder;

namespace GymBeam.AdminManager.Tests;

public class BotRegistryIntegrationTests : IDisposable
{
    private const string Password = "registry-test-password";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"manager-registry-api-{Guid.NewGuid():N}");

    [Fact]
    public async Task BotsApi_ReturnsImportedBot1AndBot2OnlyForAuthenticatedAdmin()
    {
        CreateExistingInstances();
        await using WebApplication application = BuildApplication(GetFreePort());
        await application.StartAsync();
        using HttpClient client = CreateClient(application);

        using HttpResponseMessage unauthorized = await client.GetAsync("/api/bots");
        using HttpResponseMessage unauthorizedDashboard = await client.GetAsync("/");
        using HttpResponseMessage unauthorizedLifecycle = await client.PostAsync("/api/bots/bot1/start", null);
        string sessionCookie = await LoginAsync(client);
        using var missingCsrfRequest = new HttpRequestMessage(HttpMethod.Post, "/api/bots/bot1/start");
        missingCsrfRequest.Headers.Add("Cookie", sessionCookie);
        using HttpResponseMessage missingCsrfResponse = await client.SendAsync(missingCsrfRequest);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/bots");
        request.Headers.Add("Cookie", sessionCookie);
        using HttpResponseMessage response = await client.SendAsync(request);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using var dashboardRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        dashboardRequest.Headers.Add("Cookie", sessionCookie);
        using HttpResponseMessage dashboard = await client.SendAsync(dashboardRequest);
        string dashboardHtml = await dashboard.Content.ReadAsStringAsync();
        using var spoofedQueryRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/bots?container=gymbeam-caddy&name=../gymbeam-admin-manager");
        spoofedQueryRequest.Headers.Add("Cookie", sessionCookie);
        using HttpResponseMessage spoofedQueryResponse = await client.SendAsync(spoofedQueryRequest);
        using JsonDocument spoofedQueryBody = JsonDocument.Parse(
            await spoofedQueryResponse.Content.ReadAsStringAsync());
        using var arbitraryContainerRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/containers/gymbeam-caddy");
        arbitraryContainerRequest.Headers.Add("Cookie", sessionCookie);
        using HttpResponseMessage arbitraryContainerResponse = await client.SendAsync(arbitraryContainerRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, unauthorizedDashboard.StatusCode);
        Assert.Equal("/login", unauthorizedDashboard.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedLifecycle.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrfResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, body.RootElement.GetArrayLength());
        Assert.Contains(body.RootElement.EnumerateArray(), bot => bot.GetProperty("id").GetString() == "bot1");
        Assert.Contains(body.RootElement.EnumerateArray(), bot => bot.GetProperty("id").GetString() == "bot2");
        JsonElement bot1 = Assert.Single(body.RootElement.EnumerateArray(), bot => bot.GetProperty("id").GetString() == "bot1");
        JsonElement bot2 = Assert.Single(body.RootElement.EnumerateArray(), bot => bot.GetProperty("id").GetString() == "bot2");
        Assert.Equal("running", bot1.GetProperty("state").GetString());
        Assert.Equal("unknown", bot2.GetProperty("state").GetString());
        Assert.False(bot1.TryGetProperty("instancePath", out _));
        Assert.False(bot1.TryGetProperty("containerName", out _));
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Equal("text/html", dashboard.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Bot 1", dashboardHtml);
        Assert.Contains("running", dashboardHtml);
        Assert.Contains("unknown", dashboardHtml);
        Assert.Equal(HttpStatusCode.OK, spoofedQueryResponse.StatusCode);
        Assert.Equal(body.RootElement.GetRawText(), spoofedQueryBody.RootElement.GetRawText());
        Assert.Equal(HttpStatusCode.NotFound, arbitraryContainerResponse.StatusCode);

        (string csrfToken, string csrfCookie) = await GetAuthenticatedCsrfAsync(client, sessionCookie);
        using var lifecycleRequest = new HttpRequestMessage(HttpMethod.Post, "/api/bots/bot1/restart");
        lifecycleRequest.Headers.Add("Cookie", $"{sessionCookie}; {csrfCookie}");
        lifecycleRequest.Headers.Add("X-CSRF-Token", csrfToken);
        using HttpResponseMessage lifecycleResponse = await client.SendAsync(lifecycleRequest);
        using JsonDocument lifecycleBody = JsonDocument.Parse(
            await lifecycleResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, lifecycleResponse.StatusCode);
        Assert.Equal("succeeded", lifecycleBody.RootElement.GetProperty("outcome").GetString());

        using var progressRequest = new HttpRequestMessage(HttpMethod.Get, "/api/bots/bot1/operations/latest");
        progressRequest.Headers.Add("Cookie", sessionCookie);
        using HttpResponseMessage progressResponse = await client.SendAsync(progressRequest);
        Assert.Equal(HttpStatusCode.OK, progressResponse.StatusCode);

        using var logsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/bots/bot1/logs?tail=100");
        logsRequest.Headers.Add("Cookie", sessionCookie);
        using HttpResponseMessage logsResponse = await client.SendAsync(logsRequest);
        string rawLogs = await logsResponse.Content.ReadAsStringAsync();
        using JsonDocument logsBody = JsonDocument.Parse(rawLogs);
        Assert.Equal(HttpStatusCode.OK, logsResponse.StatusCode);
        Assert.Equal("safe <script> text", logsBody.RootElement.GetProperty("logs").GetString());
        Assert.Equal("application/json", logsResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", Assert.Single(logsResponse.Headers.GetValues("X-Content-Type-Options")));

        const string submittedSecret = "new=p a s s \"quoted\" Україна";
        using var credentialRequest = new HttpRequestMessage(HttpMethod.Put, "/api/bots/bot1/credentials")
        {
            Content = JsonContent.Create(new CredentialUpdateRequest(
                GymBeamLogin: null,
                GymBeamPassword: submittedSecret,
                TelegramBotToken: null,
                TelegramChatId: null,
                BotAdminUser: null,
                BotAdminPassword: null,
                BotAdminTokenSecret: null))
        };
        credentialRequest.Headers.Add("Cookie", $"{sessionCookie}; {csrfCookie}");
        credentialRequest.Headers.Add("X-CSRF-Token", csrfToken);
        using HttpResponseMessage credentialResponse = await client.SendAsync(credentialRequest);
        string credentialBody = await credentialResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, credentialResponse.StatusCode);
        Assert.DoesNotContain(submittedSecret, credentialBody);
        using var credentialReadRequest = new HttpRequestMessage(HttpMethod.Get, "/api/bots/bot1/credentials");
        credentialReadRequest.Headers.Add("Cookie", sessionCookie);
        using HttpResponseMessage credentialReadResponse = await client.SendAsync(credentialReadRequest);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, credentialReadResponse.StatusCode);
    }

    private WebApplication BuildApplication(int port)
    {
        var security = new AdminSecurityOptions(
            "manager-admin",
            PasswordHasher.Hash(Password),
            Enumerable.Repeat((byte)42, 32).ToArray(),
            TimeSpan.FromMinutes(60),
            5,
            TimeSpan.FromMinutes(1));
        var options = new AdminManagerOptions(
            IPAddress.Loopback,
            port,
            "admin.example.com",
            security,
            Path.Combine(_directory, "storage"),
            Path.Combine(_directory, "instances"));
        return AdminManagerApplication.Build(
            options,
            dockerStatusReader: new FakeDockerStatusReader(),
            dockerLifecycleController: new FakeDockerLifecycleController(),
            dockerLogReader: new FakeDockerLogReader());
    }

    private async Task<string> LoginAsync(HttpClient client)
    {
        using HttpResponseMessage csrf = await client.GetAsync("/api/auth/csrf");
        using JsonDocument csrfBody = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        string csrfToken = csrfBody.RootElement.GetProperty("csrfToken").GetString()!;
        string csrfCookie = ExtractCookie(GetSetCookie(csrf, "gb_manager_csrf"));
        using var login = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username = "manager-admin", password = Password })
        };
        login.Headers.Add("Cookie", csrfCookie);
        login.Headers.Add("X-CSRF-Token", csrfToken);
        using HttpResponseMessage response = await client.SendAsync(login);
        return ExtractCookie(GetSetCookie(response, "gb_manager_session"));
    }

    private static async Task<(string Token, string Cookie)> GetAuthenticatedCsrfAsync(
        HttpClient client,
        string sessionCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/csrf");
        request.Headers.Add("Cookie", sessionCookie);
        using HttpResponseMessage response = await client.SendAsync(request);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (
            body.RootElement.GetProperty("csrfToken").GetString()!,
            ExtractCookie(GetSetCookie(response, "gb_manager_csrf")));
    }

    private void CreateExistingInstances()
    {
        foreach (string id in new[] { "bot1", "bot2" })
        {
            string botPath = Path.Combine(_directory, "instances", id);
            Directory.CreateDirectory(botPath);
            File.WriteAllText(Path.Combine(botPath, ".env"), "secret=value");
            File.WriteAllText(Path.Combine(botPath, "appconfig.json"), "{}");
        }
    }

    private static HttpClient CreateClient(WebApplication application)
    {
        return new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(application.Urls.Single())
        };
    }

    private static string GetSetCookie(HttpResponseMessage response, string name)
    {
        return Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith($"{name}=", StringComparison.Ordinal));
    }

    private static string ExtractCookie(string setCookie) => setCookie.Split(';', 2)[0];

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeDockerStatusReader : IDockerStatusReader
    {
        public Task<IReadOnlyDictionary<string, BotRuntimeStatus>> GetStatusesAsync(
            IReadOnlyList<ManagedBot> bots,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<string, BotRuntimeStatus> statuses = new Dictionary<string, BotRuntimeStatus>
            {
                ["bot1"] = new(
                    "running",
                    "healthy",
                    TimeSpan.FromHours(2),
                    new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero),
                    "available"),
                ["bot2"] = BotRuntimeStatus.Unknown("inspect-failed")
            };
            return Task.FromResult(statuses);
        }
    }

    private sealed class FakeDockerLifecycleController : IDockerLifecycleController
    {
        public Task<DockerLifecycleResult> ExecuteAsync(
            ManagedBot bot,
            BotLifecycleAction action,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DockerLifecycleResult("succeeded", $"{action} completed"));
    }

    private sealed class FakeDockerLogReader : IDockerLogReader
    {
        public Task<DockerLogResult> ReadAsync(
            ManagedBot bot,
            int tail,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DockerLogResult("succeeded", "safe <script> text"));
    }
}
