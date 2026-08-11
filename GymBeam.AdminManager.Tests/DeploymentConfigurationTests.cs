namespace GymBeam.AdminManager.Tests;

public class DeploymentConfigurationTests
{
    [Fact]
    public void Dockerfile_BuildsStandaloneNet8AspNetImage()
    {
        string dockerfile = ReadRootFile(Path.Combine("GymBeam.AdminManager", "Dockerfile"));

        Assert.Contains("mcr.microsoft.com/dotnet/sdk:8.0", dockerfile);
        Assert.Contains("mcr.microsoft.com/dotnet/aspnet:8.0", dockerfile);
        Assert.Contains("GymBeam.AdminManager.csproj", dockerfile);
        Assert.Contains("chown app:app /app/data", dockerfile);
        Assert.Contains("chmod 700 /app/data", dockerfile);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"GymBeam.AdminManager.dll\"]", dockerfile);
    }

    [Fact]
    public void Compose_DefinesIsolatedHealthyManagerWithoutPublishedPort()
    {
        string compose = ReadRootFile("docker-compose.yml");
        string managerService = SliceBetween(
            compose,
            "  gymbeam-admin-manager:",
            "  caddy:");

        Assert.Contains("dockerfile: GymBeam.AdminManager/Dockerfile", managerService);
        Assert.Contains("ADMIN_MANAGER_BIND_ADDRESS", managerService);
        Assert.Contains("ADMIN_MANAGER_PORT", managerService);
        Assert.Contains("ADMIN_MANAGER_PUBLIC_HOST", managerService);
        Assert.Contains("ADMIN_MANAGER_ADMIN_USERNAME", managerService);
        Assert.Contains("ADMIN_MANAGER_ADMIN_PASSWORD_HASH", managerService);
        Assert.Contains("ADMIN_MANAGER_SESSION_SIGNING_KEY", managerService);
        Assert.Contains("ADMIN_MANAGER_SESSION_LIFETIME_MINUTES", managerService);
        Assert.Contains("ADMIN_MANAGER_LOGIN_MAX_ATTEMPTS", managerService);
        Assert.Contains("ADMIN_MANAGER_LOGIN_WINDOW_SECONDS", managerService);
        Assert.Contains("ADMIN_MANAGER_STORAGE_PATH: \"/app/data\"", managerService);
        Assert.Contains("ADMIN_MANAGER_INSTANCES_PATH: \"/srv/gymbeam/instances\"", managerService);
        Assert.Contains("ADMIN_MANAGER_DOCKER_SOCKET_PATH: \"/var/run/docker.sock\"", managerService);
        Assert.Contains("ADMIN_MANAGER_DOCKER_TIMEOUT_SECONDS:", managerService);
        Assert.Contains("ADMIN_MANAGER_DOCKER_HEALTH_TIMEOUT_SECONDS:", managerService);
        Assert.Contains("ADMIN_MANAGER_DOCKER_HEALTH_POLL_MILLISECONDS:", managerService);
        Assert.Contains("ADMIN_MANAGER_DOCKER_MAX_LOG_BYTES:", managerService);
        Assert.Contains("admin_manager_data:/app/data", managerService);
        Assert.Contains("/var/run/docker.sock:/var/run/docker.sock", managerService);
        Assert.Contains("group_add:", managerService);
        Assert.Contains("${DOCKER_SOCKET_GID:-0}", managerService);
        Assert.DoesNotContain("runtime-data/admin-manager", managerService);
        Assert.Contains("./instances:/srv/gymbeam/instances", managerService);
        Assert.DoesNotContain("./instances:/srv/gymbeam/instances:ro", managerService);
        Assert.DoesNotContain("ADMIN_MANAGER_ADMIN_PASSWORD:", managerService);
        Assert.DoesNotContain("change-me", managerService, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expose:", managerService);
        Assert.Contains("\"8080\"", managerService);
        Assert.Contains("http://127.0.0.1:8080/healthz", managerService);
        Assert.DoesNotContain("ports:", managerService);
        Assert.DoesNotContain("8080:8080", managerService);
    }

    [Fact]
    public void Compose_BotsReloadAtomicallyReplacedEnvFromMountedInstanceDirectory()
    {
        string compose = ReadRootFile("docker-compose.yml");
        string common = compose[..compose.IndexOf("services:", StringComparison.Ordinal)];
        string bot1 = SliceBetween(compose, "  gymbeam-bot-1:", "  gymbeam-bot-2:");
        string bot2 = SliceBetween(compose, "  gymbeam-bot-2:", "  gymbeam-admin-manager:");

        Assert.Contains("GYMBEAM_ENV_PATH: /app/instance/.env", common);
        Assert.Contains("./instances/bot1:/app/instance:ro", bot1);
        Assert.Contains("./instances/bot2:/app/instance:ro", bot2);
        Assert.Contains("./instances/bot1/.env:/app/instance/.env", bot1);
        Assert.Contains("./instances/bot2/.env:/app/instance/.env", bot2);
        Assert.DoesNotContain("env_file:", bot1);
        Assert.DoesNotContain("env_file:", bot2);
    }

    [Fact]
    public void Compose_OnlyBotsCarryTheCompleteManagedIdentityLabels()
    {
        string compose = ReadRootFile("docker-compose.yml");
        string bot1Service = SliceBetween(compose, "  gymbeam-bot-1:", "  gymbeam-bot-2:");
        string bot2Service = SliceBetween(compose, "  gymbeam-bot-2:", "  gymbeam-admin-manager:");
        string managerService = SliceBetween(compose, "  gymbeam-admin-manager:", "  caddy:");
        string caddyService = compose[compose.IndexOf("  caddy:", StringComparison.Ordinal)..];

        AssertManagedBotLabels(bot1Service, "bot1");
        AssertManagedBotLabels(bot2Service, "bot2");
        Assert.DoesNotContain("com.gymbeam.managed", managerService);
        Assert.DoesNotContain("com.gymbeam.managed", caddyService);
    }

    [Fact]
    public void Compose_CaddyWaitsForHealthyManager()
    {
        string compose = ReadRootFile("docker-compose.yml");
        string caddyService = compose[compose.IndexOf("  caddy:", StringComparison.Ordinal)..];

        Assert.Contains("gymbeam-admin-manager:", caddyService);
        Assert.Contains("condition: service_healthy", caddyService);
    }

    [Fact]
    public void Caddy_RoutesAdminSubdomainToManagerOnly()
    {
        string caddyfile = ReadRootFile("Caddyfile");

        Assert.Contains("http://{$ADMIN_MANAGER_PUBLIC_HOST:admin.mapa-svietidiel.sk}", caddyfile);
        Assert.Contains("{$ADMIN_MANAGER_PUBLIC_HOST:admin.mapa-svietidiel.sk} {", caddyfile);
        Assert.Contains("reverse_proxy gymbeam-admin-manager:8080", caddyfile);
        Assert.Contains("Strict-Transport-Security", caddyfile);
    }

    private static string ReadRootFile(string relativePath)
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null && !File.Exists(Path.Combine(current, "GymBeamShiftsController.sln")))
        {
            current = Directory.GetParent(current)?.FullName;
        }

        Assert.False(string.IsNullOrEmpty(current));
        return File.ReadAllText(Path.Combine(current!, relativePath));
    }

    private static string SliceBetween(string value, string start, string end)
    {
        int startIndex = value.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing expected section: {start}");
        int endIndex = value.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing expected section: {end}");
        return value[startIndex..endIndex];
    }

    private static void AssertManagedBotLabels(string service, string botId)
    {
        Assert.Contains("com.gymbeam.managed: \"true\"", service);
        Assert.Contains($"com.gymbeam.bot-id: \"{botId}\"", service);
        Assert.Contains("com.gymbeam.role: \"bot\"", service);
    }
}
