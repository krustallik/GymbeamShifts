using GymBeam.AdminManager.Configuration;
using GymBeam.AdminManager.Security;
using Microsoft.Extensions.Configuration;

namespace GymBeam.AdminManager.Tests;

public class AdminManagerConfigurationTests
{
    [Fact]
    public void Load_WithValidEnvironmentConfiguration_ReturnsOptions()
    {
        IConfiguration configuration = CreateConfiguration(ValidValues());

        AdminManagerOptions options = AdminManagerConfiguration.Load(configuration);

        Assert.Equal("127.0.0.1", options.BindAddress.ToString());
        Assert.Equal(9080, options.Port);
        Assert.Equal("admin.example.com", options.PublicHost);
        Assert.Equal("manager-admin", options.Security.Username);
        Assert.Equal(TimeSpan.FromMinutes(60), options.Security.SessionLifetime);
        Assert.Equal(5, options.Security.LoginMaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Security.LoginWindow);
        Assert.Equal(32, options.Security.SessionSigningKey.Length);
        Assert.Equal(Path.GetFullPath("manager-data"), options.StoragePath);
        Assert.Equal(Path.GetFullPath("instances"), options.InstancesPath);
        Assert.Equal("/var/run/docker.sock", options.Docker.SocketPath);
        Assert.Equal(TimeSpan.FromSeconds(3), options.Docker.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Docker.HealthTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), options.Docker.HealthPollInterval);
        Assert.Equal(262144, options.Docker.MaximumLogResponseBytes);
        Assert.Equal("example.com", options.Provisioning.BaseDomain);
        Assert.Equal("gymbeam-internal", options.Provisioning.DockerNetwork);
    }

    [Theory]
    [InlineData("BIND_ADDRESS")]
    [InlineData("PORT")]
    [InlineData("PUBLIC_HOST")]
    [InlineData("ADMIN_USERNAME")]
    [InlineData("ADMIN_PASSWORD_HASH")]
    [InlineData("SESSION_SIGNING_KEY")]
    [InlineData("SESSION_LIFETIME_MINUTES")]
    [InlineData("LOGIN_MAX_ATTEMPTS")]
    [InlineData("LOGIN_WINDOW_SECONDS")]
    [InlineData("STORAGE_PATH")]
    [InlineData("INSTANCES_PATH")]
    [InlineData("DOCKER_SOCKET_PATH")]
    [InlineData("DOCKER_TIMEOUT_SECONDS")]
    [InlineData("DOCKER_HEALTH_TIMEOUT_SECONDS")]
    [InlineData("DOCKER_HEALTH_POLL_MILLISECONDS")]
    [InlineData("DOCKER_MAX_LOG_BYTES")]
    [InlineData("PROVISIONING_BASE_DOMAIN")]
    [InlineData("PROVISIONING_DOCKER_IMAGE")]
    [InlineData("PROVISIONING_DOCKER_NETWORK")]
    [InlineData("PROVISIONING_DOCKER_INSTANCES_PATH")]
    [InlineData("PROVISIONING_CADDY_ADMIN_SOCKET_PATH")]
    [InlineData("PROVISIONING_CADDYFILE_PATH")]
    [InlineData("PROVISIONING_CADDY_ROUTES_PATH")]
    [InlineData("PROVISIONING_BOT_TEMPLATE_PATH")]
    [InlineData("PROVISIONING_MIN_FREE_DISK_BYTES")]
    public void Load_WhenRequiredValueIsMissing_FailsFast(string missingKey)
    {
        Dictionary<string, string?> values = ValidValues();
        values[missingKey] = null;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => AdminManagerConfiguration.Load(CreateConfiguration(values)));

        Assert.Contains(missingKey, exception.Message);
    }

    [Theory]
    [InlineData("not-an-ip", "9080", "admin.example.com", "BIND_ADDRESS")]
    [InlineData("127.0.0.1", "0", "admin.example.com", "PORT")]
    [InlineData("127.0.0.1", "65536", "admin.example.com", "PORT")]
    [InlineData("127.0.0.1", "9080", "https://admin.example.com/path", "PUBLIC_HOST")]
    public void Load_WhenValueIsInvalid_FailsFast(
        string bindAddress,
        string port,
        string publicHost,
        string expectedKey)
    {
        Dictionary<string, string?> values = ValidValues();
        values["BIND_ADDRESS"] = bindAddress;
        values["PORT"] = port;
        values["PUBLIC_HOST"] = publicHost;
        IConfiguration configuration = CreateConfiguration(values);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => AdminManagerConfiguration.Load(configuration));

        Assert.Contains(expectedKey, exception.Message);
    }

    [Fact]
    public void Load_WhenSecretsAreInvalid_DoesNotEchoSecretValues()
    {
        Dictionary<string, string?> values = ValidValues();
        values["ADMIN_PASSWORD_HASH"] = "plaintext-super-secret";
        values["SESSION_SIGNING_KEY"] = "short-signing-secret";

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => AdminManagerConfiguration.Load(CreateConfiguration(values)));

        Assert.Contains("ADMIN_PASSWORD_HASH", exception.Message);
        Assert.Contains("SESSION_SIGNING_KEY", exception.Message);
        Assert.DoesNotContain("plaintext-super-secret", exception.Message);
        Assert.DoesNotContain("short-signing-secret", exception.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1441")]
    [InlineData("not-a-number")]
    public void Load_WhenSessionLifetimeIsInvalid_FailsFast(string value)
    {
        Dictionary<string, string?> values = ValidValues();
        values["SESSION_LIFETIME_MINUTES"] = value;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => AdminManagerConfiguration.Load(CreateConfiguration(values)));

        Assert.Contains("SESSION_LIFETIME_MINUTES", exception.Message);
    }

    [Theory]
    [InlineData("relative/docker.sock", "3", "DOCKER_SOCKET_PATH")]
    [InlineData("/var/run/docker.sock", "0", "DOCKER_TIMEOUT_SECONDS")]
    [InlineData("/var/run/docker.sock", "31", "DOCKER_TIMEOUT_SECONDS")]
    public void Load_WhenDockerConfigurationIsInvalid_FailsFast(
        string socketPath,
        string timeout,
        string expectedKey)
    {
        Dictionary<string, string?> values = ValidValues();
        values["DOCKER_SOCKET_PATH"] = socketPath;
        values["DOCKER_TIMEOUT_SECONDS"] = timeout;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => AdminManagerConfiguration.Load(CreateConfiguration(values)));

        Assert.Contains(expectedKey, exception.Message);
    }

    [Theory]
    [InlineData("DOCKER_HEALTH_TIMEOUT_SECONDS", "4")]
    [InlineData("DOCKER_HEALTH_TIMEOUT_SECONDS", "301")]
    [InlineData("DOCKER_HEALTH_POLL_MILLISECONDS", "99")]
    [InlineData("DOCKER_HEALTH_POLL_MILLISECONDS", "5001")]
    [InlineData("DOCKER_MAX_LOG_BYTES", "4095")]
    [InlineData("DOCKER_MAX_LOG_BYTES", "1048577")]
    public void Load_WhenDockerLifecycleOrLogLimitIsInvalid_FailsFast(string key, string value)
    {
        Dictionary<string, string?> values = ValidValues();
        values[key] = value;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => AdminManagerConfiguration.Load(CreateConfiguration(values)));

        Assert.Contains(key, exception.Message);
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }

    private static Dictionary<string, string?> ValidValues()
    {
        return new Dictionary<string, string?>
        {
            ["BIND_ADDRESS"] = "127.0.0.1",
            ["PORT"] = "9080",
            ["PUBLIC_HOST"] = "admin.example.com",
            ["ADMIN_USERNAME"] = "manager-admin",
            ["ADMIN_PASSWORD_HASH"] = PasswordHasher.Hash("test-password"),
            ["SESSION_SIGNING_KEY"] = Convert.ToBase64String(new byte[32]),
            ["SESSION_LIFETIME_MINUTES"] = "60",
            ["LOGIN_MAX_ATTEMPTS"] = "5",
            ["LOGIN_WINDOW_SECONDS"] = "60",
            ["STORAGE_PATH"] = Path.GetFullPath("manager-data"),
            ["INSTANCES_PATH"] = Path.GetFullPath("instances"),
            ["DOCKER_SOCKET_PATH"] = "/var/run/docker.sock",
            ["DOCKER_TIMEOUT_SECONDS"] = "3",
            ["DOCKER_HEALTH_TIMEOUT_SECONDS"] = "60",
            ["DOCKER_HEALTH_POLL_MILLISECONDS"] = "1000",
            ["DOCKER_MAX_LOG_BYTES"] = "262144",
            ["PROVISIONING_BASE_DOMAIN"] = "example.com",
            ["PROVISIONING_DOCKER_IMAGE"] = "gymbeam-shifts-bot:latest",
            ["PROVISIONING_DOCKER_NETWORK"] = "gymbeam-internal",
            ["PROVISIONING_DOCKER_INSTANCES_PATH"] = "/opt/gymbeam/instances",
            ["PROVISIONING_CADDY_ADMIN_SOCKET_PATH"] = Path.GetFullPath("caddy-admin.sock"),
            ["PROVISIONING_CADDYFILE_PATH"] = Path.GetFullPath("Caddyfile"),
            ["PROVISIONING_CADDY_ROUTES_PATH"] = Path.GetFullPath("caddy-dynamic"),
            ["PROVISIONING_BOT_TEMPLATE_PATH"] = Path.GetFullPath("instances/bot1/appconfig.json"),
            ["PROVISIONING_MIN_FREE_DISK_BYTES"] = "134217728"
        };
    }
}
