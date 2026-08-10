using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using GymBeam.AdminManager.Configuration;
using GymBeam.AdminManager.Security;
using Microsoft.AspNetCore.Builder;

namespace GymBeam.AdminManager.Tests;

public class AuthenticationIntegrationTests : IDisposable
{
    private const string Username = "manager-admin";
    private const string Password = "strong-test-password";
    private readonly string _storagePath = Path.Combine(Path.GetTempPath(), $"manager-auth-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Login_WithValidCredentialsSetsSecureSignedSessionAndUnlocksProtectedApi()
    {
        await using WebApplication application = BuildApplication(GetFreePort());
        await application.StartAsync();
        using HttpClient client = CreateClient(application);

        using HttpResponseMessage login = await LoginAsync(client, Username, Password);
        string setCookie = GetSetCookie(login, "gb_manager_session");
        string cookie = ExtractCookie(setCookie);
        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meRequest.Headers.Add("Cookie", cookie);
        using HttpResponseMessage me = await client.SendAsync(meRequest);
        using JsonDocument body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(Username, body.RootElement.GetProperty("username").GetString());
    }

    [Fact]
    public async Task Login_WithInvalidCredentialsReturnsGenericUnauthorized()
    {
        await using WebApplication application = BuildApplication(GetFreePort());
        await application.StartAsync();
        using HttpClient client = CreateClient(application);

        using HttpResponseMessage response = await LoginAsync(client, Username, "wrong-password");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Invalid credentials", body);
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values) ? values : [],
            value => value.StartsWith("gb_manager_session=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProtectedApi_WithoutSessionReturnsUnauthorized()
    {
        await using WebApplication application = BuildApplication(GetFreePort());
        await application.StartAsync();
        using HttpClient client = CreateClient(application);

        using HttpResponseMessage response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesSessionAndExpiresCookie()
    {
        await using WebApplication application = BuildApplication(GetFreePort());
        await application.StartAsync();
        using HttpClient client = CreateClient(application);
        using HttpResponseMessage login = await LoginAsync(client, Username, Password);
        string cookie = ExtractCookie(GetSetCookie(login, "gb_manager_session"));
        string csrfCookie = ExtractCookie(GetSetCookie(login, "gb_manager_csrf"));
        using JsonDocument loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        string csrfToken = loginBody.RootElement.GetProperty("csrfToken").GetString()!;
        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logoutRequest.Headers.Add("Cookie", $"{cookie}; {csrfCookie}");
        logoutRequest.Headers.Add("X-CSRF-Token", csrfToken);

        using HttpResponseMessage logout = await client.SendAsync(logoutRequest);
        string expiredCookie = GetSetCookie(logout, "gb_manager_session");
        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meRequest.Headers.Add("Cookie", cookie);
        using HttpResponseMessage me = await client.SendAsync(meRequest);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains("Max-Age=0", expiredCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task Session_RemainsValidAfterApplicationRestartAndExpiresAtConfiguredTime()
    {
        string cookie;
        await using (WebApplication first = BuildApplication(GetFreePort()))
        {
            await first.StartAsync();
            using HttpClient client = CreateClient(first);
            using HttpResponseMessage login = await LoginAsync(client, Username, Password);
            cookie = ExtractCookie(GetSetCookie(login, "gb_manager_session"));
        }

        await using WebApplication restarted = BuildApplication(GetFreePort());
        await restarted.StartAsync();
        using HttpClient restartedClient = CreateClient(restarted);
        using var validRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        validRequest.Headers.Add("Cookie", cookie);
        using HttpResponseMessage valid = await restartedClient.SendAsync(validRequest);

        _clock.Advance(TimeSpan.FromMinutes(61));
        using var expiredRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        expiredRequest.Headers.Add("Cookie", cookie);
        using HttpResponseMessage expired = await restartedClient.SendAsync(expiredRequest);

        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    private WebApplication BuildApplication(int port)
    {
        var security = new AdminSecurityOptions(
            Username,
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
            _storagePath,
            Path.GetTempPath());
        return AdminManagerApplication.Build(options, timeProvider: _clock);
    }

    private static HttpClient CreateClient(WebApplication application)
    {
        string address = application.Urls.Single();
        return new HttpClient(new HttpClientHandler { UseCookies = false })
        {
            BaseAddress = new Uri(address)
        };
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password)
    {
        using HttpResponseMessage csrf = await client.GetAsync("/api/auth/csrf");
        using JsonDocument body = JsonDocument.Parse(await csrf.Content.ReadAsStringAsync());
        string token = body.RootElement.GetProperty("csrfToken").GetString()!;
        string cookie = ExtractCookie(GetSetCookie(csrf, "gb_manager_csrf"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { username, password })
        };
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("X-CSRF-Token", token);
        return await client.SendAsync(request);
    }

    private static string GetSetCookie(HttpResponseMessage response, string name)
    {
        return Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith($"{name}=", StringComparison.Ordinal));
    }

    private static string ExtractCookie(string setCookie)
    {
        return setCookie.Split(';', 2)[0];
    }

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
