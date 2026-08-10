using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using GymBeam.AdminManager.Configuration;
using GymBeam.AdminManager.Security;
using Microsoft.AspNetCore.Builder;

namespace GymBeam.AdminManager.Tests;

public class AuthenticationSecurityIntegrationTests : IDisposable
{
    private const string Password = "security-test-password";
    private readonly string _storagePath = Path.Combine(Path.GetTempPath(), $"manager-security-{Guid.NewGuid():N}");

    [Fact]
    public async Task Login_WithoutCsrfIsRejected()
    {
        await using WebApplication application = BuildApplication(GetFreePort(), maxAttempts: 5);
        await application.StartAsync();
        using HttpClient client = CreateClient(application);

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username = "manager-admin", password = Password });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Login_WhenLimitIsExceededReturnsTooManyRequests()
    {
        await using WebApplication application = BuildApplication(GetFreePort(), maxAttempts: 3);
        await application.StartAsync();
        using HttpClient client = CreateClient(application);
        (string token, string cookie) = await GetCsrfAsync(client);

        for (int index = 0; index < 3; index++)
        {
            using HttpResponseMessage attempt = await SendLoginAsync(client, token, cookie, "wrong-password");
            Assert.Equal(HttpStatusCode.Unauthorized, attempt.StatusCode);
        }

        using HttpResponseMessage limited = await SendLoginAsync(client, token, cookie, "wrong-password");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task LoginRateLimit_UsesSanitizedForwardedClientAddressFromPrivateProxy()
    {
        await using WebApplication application = BuildApplication(GetFreePort(), maxAttempts: 1);
        await application.StartAsync();
        using HttpClient client = CreateClient(application);
        (string token, string cookie) = await GetCsrfAsync(client);

        using HttpResponseMessage first = await SendLoginAsync(
            client, token, cookie, "wrong-password", "198.51.100.10");
        using HttpResponseMessage secondClient = await SendLoginAsync(
            client, token, cookie, "wrong-password", "198.51.100.11");
        using HttpResponseMessage spoofedChain = await SendLoginAsync(
            client, token, cookie, "wrong-password", "198.51.100.12, 198.51.100.13");

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, secondClient.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, spoofedChain.StatusCode);
    }

    [Fact]
    public async Task Logout_WithLoginCsrfInsteadOfSessionBoundCsrfIsRejected()
    {
        await using WebApplication application = BuildApplication(GetFreePort(), maxAttempts: 5);
        await application.StartAsync();
        using HttpClient client = CreateClient(application);
        (string loginCsrf, string loginCsrfCookie) = await GetCsrfAsync(client);
        using HttpResponseMessage login = await SendLoginAsync(client, loginCsrf, loginCsrfCookie, Password);
        string sessionCookie = ExtractCookie(GetSetCookie(login, "gb_manager_session"));

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logout.Headers.Add("Cookie", $"{sessionCookie}; {loginCsrfCookie}");
        logout.Headers.Add("X-CSRF-Token", loginCsrf);
        using HttpResponseMessage response = await client.SendAsync(logout);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticationAuditContainsOutcomesButNoCredentialsOrTokens()
    {
        await using WebApplication application = BuildApplication(GetFreePort(), maxAttempts: 5);
        await application.StartAsync();
        using HttpClient client = CreateClient(application);
        (string token, string cookie) = await GetCsrfAsync(client);
        using HttpResponseMessage failed = await SendLoginAsync(client, token, cookie, "wrong-password");
        using HttpResponseMessage succeeded = await SendLoginAsync(client, token, cookie, Password);

        string audit = await File.ReadAllTextAsync(Path.Combine(_storagePath, "audit.jsonl"));

        Assert.Contains("failure", audit);
        Assert.Contains("success", audit);
        Assert.DoesNotContain(Password, audit);
        Assert.DoesNotContain("wrong-password", audit);
        Assert.DoesNotContain(token, audit);
        Assert.DoesNotContain(ExtractCookie(GetSetCookie(succeeded, "gb_manager_session")), audit);
    }

    private WebApplication BuildApplication(int port, int maxAttempts)
    {
        var security = new AdminSecurityOptions(
            "manager-admin",
            PasswordHasher.Hash(Password),
            Enumerable.Repeat((byte)42, 32).ToArray(),
            TimeSpan.FromMinutes(60),
            maxAttempts,
            TimeSpan.FromMinutes(1));
        var options = new AdminManagerOptions(
            IPAddress.Loopback,
            port,
            "admin.example.com",
            security,
            _storagePath,
            Path.GetTempPath());
        return AdminManagerApplication.Build(options);
    }

    private static async Task<(string Token, string Cookie)> GetCsrfAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/auth/csrf");
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (
            body.RootElement.GetProperty("csrfToken").GetString()!,
            ExtractCookie(GetSetCookie(response, "gb_manager_csrf")));
    }

    private static Task<HttpResponseMessage> SendLoginAsync(
        HttpClient client,
        string csrfToken,
        string csrfCookie,
        string password,
        string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username = "manager-admin", password })
        };
        request.Headers.Add("Cookie", csrfCookie);
        request.Headers.Add("X-CSRF-Token", csrfToken);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }
        return client.SendAsync(request);
    }

    private static HttpClient CreateClient(WebApplication application)
    {
        return new HttpClient(new HttpClientHandler { UseCookies = false })
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
        if (Directory.Exists(_storagePath))
        {
            Directory.Delete(_storagePath, recursive: true);
        }
    }
}
