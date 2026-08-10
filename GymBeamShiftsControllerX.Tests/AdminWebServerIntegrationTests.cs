using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using GymBeamShiftsControllerX.Models;
using GymBeamShiftsControllerX.Services;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

[Collection("MutableEnvironment")]
public class AdminWebServerIntegrationTests : IDisposable
{
    private readonly string _configFileName;
    private readonly string _configPath;
    private readonly AdminWebServer _server;
    private readonly HttpClient _client;
    private readonly CookieContainer _cookies = new CookieContainer();
    private readonly string _previousPort;
    private readonly string _previousHost;
    private readonly string _previousUser;
    private readonly string _previousPassword;
    private readonly string _previousSecret;
    private readonly string _previousLogPath;

    public AdminWebServerIntegrationTests()
    {
        int port = GetFreePort();
        _configFileName = $"appconfig.admin.{Guid.NewGuid():N}.json";
        _configPath = Path.Combine(TestPathHelper.GetWorkspaceRoot(), _configFileName);

        _previousPort = Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_PORT") ?? string.Empty;
        _previousHost = Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_HOST") ?? string.Empty;
        _previousUser = Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_USER") ?? string.Empty;
        _previousPassword = Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_PASSWORD") ?? string.Empty;
        _previousSecret = Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_TOKEN_SECRET") ?? string.Empty;
        _previousLogPath = Environment.GetEnvironmentVariable("GYMBEAM_LOG_PATH") ?? string.Empty;

        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_PORT", port.ToString());
        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_HOST", "localhost");
        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_USER", "testadmin");
        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_PASSWORD", "testpass");
        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_TOKEN_SECRET", "integration-test-secret");
        Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", Path.Combine(Path.GetTempPath(), $"gymbeam-admin-{Guid.NewGuid():N}.log"));
        ResetLoggerPath();

        File.WriteAllText(_configPath, CreateInitialConfigJson());

        var cfg = new AppConfig
        {
            Auth = new AuthSettings { Login = "x", Password = "y", LoginUrl = "https://example.com" },
            Telegram = new TelegramSettings { BotToken = "token", ChatId = "chat" },
            Timing = new TimingSettings
            {
                ShiftMinHoursAhead = 48,
                WeekendOrHolidayMinHoursAhead = 28,
                ImportantShiftNotificationCount = 4,
                ImportantShiftNotificationDelayMilliseconds = 30000
            },
            ShiftRules = new ShiftRulesSettings
            {
                TakeLunch = false,
                IncludedWeekdays = new List<string> { "Monday" },
                FavoriteShiftUsers = new List<string> { "Andrea Pavlíková" }
            }
        };

        var store = new ShiftRulesStore(cfg.ShiftRules);
        _server = new AdminWebServer(
            cfg,
            store,
            () => new BotStatusSnapshot { IsRunning = true, TotalIterations = 42 },
            _configFileName);

        _server.Start();
        Thread.Sleep(150);

        var handler = new HttpClientHandler { CookieContainer = _cookies };
        _client = new HttpClient(handler) { BaseAddress = new Uri($"http://localhost:{port}/") };
    }

    [Fact]
    public async Task GetRoot_ReturnsHtml()
    {
        var response = await _client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("GymBeam Bot Admin", body);
        Assert.Contains("weekendOrHolidayMinHoursAhead", body);
        Assert.Contains("importantShiftNotificationCount", body);
        Assert.Contains("importantShiftNotificationDelayMilliseconds", body);
        Assert.Contains("id='takeLunch'", body);
    }

    [Fact]
    public async Task Healthz_DoesNotRequireAuthentication()
    {
        var response = await _client.GetAsync("/healthz");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Login_ThroughHttpsProxy_SetsSecureSessionCookie()
    {
        var payload = JsonSerializer.Serialize(new { username = "testadmin", password = "testpass" });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/login")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await _client.SendAsync(request);
        string setCookie = string.Join(";", response.Headers.GetValues("Set-Cookie"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsSessionCookie()
    {
        var response = await LoginAsync("testadmin", "testpass");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gb_admin_session", _cookies.GetCookies(_client.BaseAddress!).Cast<Cookie>().Select(c => c.Name));
    }

    [Fact]
    public async Task Login_WithInvalidCredentials_Returns401()
    {
        var response = await LoginAsync("testadmin", "wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_FourthAttemptWithinMinute_Returns429()
    {
        await LoginAsync("bad", "bad");
        await LoginAsync("bad", "bad");
        await LoginAsync("bad", "bad");
        var response = await LoginAsync("bad", "bad");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task Status_WithoutAuth_Returns401()
    {
        var response = await _client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Status_WithAuth_ReturnsSnapshot()
    {
        await LoginAsync("testadmin", "testpass");

        var response = await _client.GetAsync("/api/status");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(document.RootElement.GetProperty("isRunning").GetBoolean());
        Assert.Equal(42, document.RootElement.GetProperty("totalIterations").GetInt64());
    }

    [Fact]
    public async Task ShiftRules_GetWithAuth_ReturnsCamelCasePayload()
    {
        await LoginAsync("testadmin", "testpass");

        var response = await _client.GetAsync("/api/shift-rules");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(document.RootElement.GetProperty("takeLunch").GetBoolean());
        Assert.Equal(48, document.RootElement.GetProperty("shiftMinHoursAhead").GetInt32());
        Assert.Equal(28, document.RootElement.GetProperty("weekendOrHolidayMinHoursAhead").GetInt32());
        Assert.Equal(4, document.RootElement.GetProperty("importantShiftNotificationCount").GetInt32());
        Assert.Equal(30000, document.RootElement.GetProperty("importantShiftNotificationDelayMilliseconds").GetInt32());
        Assert.Equal("Andrea Pavlíková", document.RootElement.GetProperty("favoriteShiftUsers")[0].GetString());
    }

    [Fact]
    public async Task ShiftRules_PutWithAuth_UpdatesStoreAndConfigFile()
    {
        await LoginAsync("testadmin", "testpass");

        var payload = """
        {
          "takeLunch": true,
          "shiftMinHoursAhead": 72,
          "weekendOrHolidayMinHoursAhead": 24,
          "importantShiftNotificationCount": 6,
          "importantShiftNotificationDelayMilliseconds": 45000,
          "includedWeekdays": ["Friday"],
          "startTimesToSkip": ["21:45"],
          "holidays": ["2026-05-08"],
          "excludedDates": ["2026-05-09"],
          "favoriteShiftUsers": ["Lukáš Fialek"]
        }
        """;
        var response = await _client.PutAsync(
            "/api/shift-rules",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var getResponse = await _client.GetAsync("/api/shift-rules");
        var json = await getResponse.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("takeLunch").GetBoolean());
        Assert.Equal(72, document.RootElement.GetProperty("shiftMinHoursAhead").GetInt32());
        Assert.Equal(24, document.RootElement.GetProperty("weekendOrHolidayMinHoursAhead").GetInt32());
        Assert.Equal(6, document.RootElement.GetProperty("importantShiftNotificationCount").GetInt32());
        Assert.Equal(45000, document.RootElement.GetProperty("importantShiftNotificationDelayMilliseconds").GetInt32());
        Assert.Equal("Friday", document.RootElement.GetProperty("includedWeekdays")[0].GetString());
        Assert.Equal("Lukáš Fialek", document.RootElement.GetProperty("favoriteShiftUsers")[0].GetString());

        var saved = await File.ReadAllTextAsync(_configPath);
        using var savedJson = JsonDocument.Parse(saved);
        Assert.True(savedJson.RootElement.GetProperty("ShiftRules").GetProperty("TakeLunch").GetBoolean());
        Assert.Equal(72, savedJson.RootElement.GetProperty("Timing").GetProperty("ShiftMinHoursAhead").GetInt32());
        Assert.Equal(24, savedJson.RootElement.GetProperty("Timing").GetProperty("WeekendOrHolidayMinHoursAhead").GetInt32());
        Assert.Equal(6, savedJson.RootElement.GetProperty("Timing").GetProperty("ImportantShiftNotificationCount").GetInt32());
        Assert.Equal(45000, savedJson.RootElement.GetProperty("Timing").GetProperty("ImportantShiftNotificationDelayMilliseconds").GetInt32());
        Assert.Equal("Lukáš Fialek", savedJson.RootElement.GetProperty("ShiftRules").GetProperty("FavoriteShiftUsers")[0].GetString());
    }

    [Fact]
    public async Task ShiftRules_PutWithNullPayload_Returns400()
    {
        await LoginAsync("testadmin", "testpass");

        var response = await _client.PutAsync(
            "/api/shift-rules",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ShiftRules_PutWithoutAuthentication_Returns401()
    {
        var response = await _client.PutAsync(
            "/api/shift-rules",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithNullPayload_Returns400()
    {
        var response = await _client.PostAsync(
            "/api/login",
            new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithMalformedJson_Returns400AndClosesResponse()
    {
        var response = await _client.PostAsync(
            "/api/login",
            new StringContent("{ malformed", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid payload", body);
    }

    [Fact]
    public async Task LoginRateLimit_UsesForwardedClientAddress()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var blockedIpRequest = CreateLoginRequest("bad", "bad");
            blockedIpRequest.Headers.Add("X-Forwarded-For", "203.0.113.10, 10.0.0.1");
            var blockedIpResponse = await _client.SendAsync(blockedIpRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, blockedIpResponse.StatusCode);
        }

        using var differentIpRequest = CreateLoginRequest("bad", "bad");
        differentIpRequest.Headers.Add("X-Forwarded-For", "203.0.113.11");
        var differentIpResponse = await _client.SendAsync(differentIpRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, differentIpResponse.StatusCode);
    }

    [Fact]
    public async Task LoginRateLimit_UsesRealIpHeaderWhenForwardedHeaderMissing()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var blockedIpRequest = CreateLoginRequest("bad", "bad");
            blockedIpRequest.Headers.Add("X-Real-IP", "198.51.100.20");
            var blockedIpResponse = await _client.SendAsync(blockedIpRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, blockedIpResponse.StatusCode);
        }

        using var differentIpRequest = CreateLoginRequest("bad", "bad");
        differentIpRequest.Headers.Add("X-Real-IP", "198.51.100.21");
        var differentIpResponse = await _client.SendAsync(differentIpRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, differentIpResponse.StatusCode);
    }

    [Fact]
    public async Task ShiftRules_PutClampsNumericSettings()
    {
        await LoginAsync("testadmin", "testpass");
        var payload = """
        {
          "shiftMinHoursAhead": 0,
          "weekendOrHolidayMinHoursAhead": 9999,
          "importantShiftNotificationCount": 99,
          "importantShiftNotificationDelayMilliseconds": -1
        }
        """;

        var response = await _client.PutAsync(
            "/api/shift-rules",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var getResponse = await _client.GetAsync("/api/shift-rules");
        using var document = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, document.RootElement.GetProperty("shiftMinHoursAhead").GetInt32());
        Assert.Equal(720, document.RootElement.GetProperty("weekendOrHolidayMinHoursAhead").GetInt32());
        Assert.Equal(20, document.RootElement.GetProperty("importantShiftNotificationCount").GetInt32());
        Assert.Equal(0, document.RootElement.GetProperty("importantShiftNotificationDelayMilliseconds").GetInt32());
    }

    [Fact]
    public async Task ShiftRules_PutReturns500WhenConfigCannotBeSaved()
    {
        await LoginAsync("testadmin", "testpass");
        File.Delete(_configPath);

        var response = await _client.PutAsync(
            "/api/shift-rules",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Logout_ExpiresSessionCookie()
    {
        await LoginAsync("testadmin", "testpass");
        var logoutResponse = await _client.PostAsync("/api/logout", new StringContent("{}", Encoding.UTF8, "application/json"));
        var statusResponse = await _client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, statusResponse.StatusCode);
    }

    [Fact]
    public async Task LogsToday_ReturnsOnlyTodayLinesWithLimit()
    {
        await LoginAsync("testadmin", "testpass");
        string logPath = Environment.GetEnvironmentVariable("GYMBEAM_LOG_PATH")!;
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        string yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
        await File.WriteAllLinesAsync(logPath, new[]
        {
            $"{yesterday} 10:00:00 - old",
            $"{today} 10:00:00 - one",
            $"{today} 10:01:00 - two",
            $"{today} 10:02:00 - three"
        });

        var response = await _client.GetAsync("/api/logs/today?limit=2");
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var lines = document.RootElement.GetProperty("lines");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Contains("two", lines[0].GetString());
        Assert.Contains("three", lines[1].GetString());
    }

    [Fact]
    public async Task LogsToday_WithoutAuthentication_Returns401()
    {
        var response = await _client.GetAsync("/api/logs/today");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UnknownRoute_Returns404()
    {
        var loginResponse = await LoginAsync("testadmin", "testpass");
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        var response = await _client.GetAsync("/api/unknown");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose()
    {
        _server.Stop();
        _client.Dispose();

        if (File.Exists(_configPath))
        {
            File.Delete(_configPath);
        }

        string? logPath = Environment.GetEnvironmentVariable("GYMBEAM_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
        {
            File.Delete(logPath);
        }

        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_PORT", string.IsNullOrEmpty(_previousPort) ? null : _previousPort);
        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_HOST", string.IsNullOrEmpty(_previousHost) ? null : _previousHost);
        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_USER", string.IsNullOrEmpty(_previousUser) ? null : _previousUser);
        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_PASSWORD", string.IsNullOrEmpty(_previousPassword) ? null : _previousPassword);
        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_TOKEN_SECRET", string.IsNullOrEmpty(_previousSecret) ? null : _previousSecret);
        Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", string.IsNullOrEmpty(_previousLogPath) ? null : _previousLogPath);
        ResetLoggerPath();
    }

    private static void ResetLoggerPath()
    {
        ReflectionTestHelper.SetStaticField(typeof(Logger), "_logFilePath", null);
    }

    private Task<HttpResponseMessage> LoginAsync(string username, string password)
    {
        var payload = JsonSerializer.Serialize(new { username, password });
        return _client.PostAsync("/api/login", new StringContent(payload, Encoding.UTF8, "application/json"));
    }

    private static HttpRequestMessage CreateLoginRequest(string username, string password)
    {
        var payload = JsonSerializer.Serialize(new { username, password });
        return new HttpRequestMessage(HttpMethod.Post, "/api/login")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string CreateInitialConfigJson()
    {
        return """
        {
          "Auth": {
            "LoginUrl": "https://example.com/login",
            "Login": "x",
            "Password": "y",
            "SuccessUrlContains": "/ok"
          },
          "Telegram": {
            "BotToken": "token",
            "ChatId": "chat"
          },
          "Timing": {
            "CheckIntervalMinutes": 2,
            "ShiftMinHoursAhead": 48,
            "WeekendOrHolidayMinHoursAhead": 28,
            "ImportantShiftNotificationCount": 4,
            "ImportantShiftNotificationDelayMilliseconds": 30000
          },
          "ShiftRules": {
            "IncludedWeekdays": ["Monday"],
            "FavoriteShiftUsers": ["Andrea Pavlíková"]
          }
        }
        """;
    }
}
