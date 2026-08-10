using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GymBeam.AdminManager.Configuration;
using GymBeam.AdminManager.Security;
using Microsoft.AspNetCore.Builder;

namespace GymBeam.AdminManager.Tests;

public class HealthEndpointTests
{
    [Fact]
    public async Task GetHealthz_ReturnsOkWithoutAuthentication()
    {
        int port = GetFreePort();
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            $"manager-health-{Guid.NewGuid():N}");
        string storagePath = Path.Combine(testRoot, "storage");
        string instancesPath = Path.Combine(testRoot, "instances");
        Directory.CreateDirectory(instancesPath);
        var security = new AdminSecurityOptions(
            "manager-admin",
            PasswordHasher.Hash("test-password"),
            new byte[32],
            TimeSpan.FromMinutes(60),
            5,
            TimeSpan.FromMinutes(1));
        var options = new AdminManagerOptions(
            IPAddress.Loopback,
            port,
            "admin.example.com",
            security,
            storagePath,
            instancesPath);

        try
        {
            await using WebApplication application = AdminManagerApplication.Build(options);
            await application.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            using HttpResponseMessage response = await client.GetAsync("/healthz");
            string body = await response.Content.ReadAsStringAsync();
            using JsonDocument json = JsonDocument.Parse(body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
