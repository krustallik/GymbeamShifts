using System.IO;
using System.Text.Json;

namespace GymBeamShiftsControllerX.Tests;

public class DeploymentConfigurationTests
{
    [Fact]
    public void Compose_UsesIsolatedRuntimeFilesForBothBots()
    {
        string compose = ReadRootFile("docker-compose.yml");

        Assert.Contains("gymbeam-bot-1:", compose);
        Assert.Contains("gymbeam-bot-2:", compose);
        Assert.Contains("GYMBEAM_ENV_PATH: /app/instance/.env", compose);
        Assert.Contains("./instances/bot1:/app/instance:ro", compose);
        Assert.Contains("./instances/bot2:/app/instance:ro", compose);
        Assert.Contains("./instances/bot1/.env:/app/instance/.env", compose);
        Assert.Contains("./instances/bot2/.env:/app/instance/.env", compose);
        Assert.DoesNotContain("env_file:", compose);
        Assert.Contains("./instances/bot1/appconfig.json:/app/appconfig.json", compose);
        Assert.Contains("./instances/bot2/appconfig.json:/app/appconfig.json", compose);
        Assert.Contains("./instances/bot1/runtime-data:/app/runtime-data", compose);
        Assert.Contains("./instances/bot2/runtime-data:/app/runtime-data", compose);
        Assert.DoesNotContain("8080:8080", compose);
    }

    [Fact]
    public void BotImage_RunsAsAppUserAndDeployMigratesLegacyRuntimeOwnership()
    {
        string dockerfile = ReadRootFile("Dockerfile");
        string deploy = ReadRootFile(Path.Combine("scripts", "deploy.sh"));

        Assert.Contains("chown -R app:app /app", dockerfile);
        Assert.Contains("COPY --from=build --chown=app:app", dockerfile);
        Assert.Contains("USER app", dockerfile);
        Assert.Contains("-v \"${ROOT_DIR}/instances:/target\"", deploy);
        Assert.Contains("chown -R app:app /target", deploy);
    }

    [Fact]
    public void ProvisionedBots_CanWriteConfigurationAndIncludeSlovakPremiumHolidays()
    {
        string provisioner = ReadRootFile(Path.Combine(
            "GymBeam.AdminManager", "Provisioning", "SafeDockerProvisioner.cs"));
        string template = ReadRootFile(Path.Combine(
            "GymBeam.AdminManager", "Provisioning", "bot-appconfig.json"));

        Assert.Contains("appconfig.json:/app/appconfig.json\"", provisioner);
        Assert.DoesNotContain("appconfig.json:/app/appconfig.json:ro", provisioner);

        using JsonDocument document = JsonDocument.Parse(template);
        string[] holidays = document.RootElement
            .GetProperty("ShiftRules")
            .GetProperty("Holidays")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();

        Assert.Equal(26, holidays.Length);
        Assert.Contains("2026-05-08", holidays);
        Assert.Contains("2026-09-15", holidays);
        Assert.Contains("2027-03-26", holidays);
        Assert.Contains("2027-03-29", holidays);
        Assert.DoesNotContain("2026-09-01", holidays);
        Assert.DoesNotContain("2026-11-17", holidays);
    }

    [Fact]
    public void Caddy_RoutesEachDomainToItsOwnBot()
    {
        string caddyfile = string.Join(
            Environment.NewLine,
            ReadRootFile("Caddyfile"),
            ReadRootFile(Path.Combine("deploy", "examples", "caddy-dynamic", "bot1.caddy")),
            ReadRootFile(Path.Combine("deploy", "examples", "caddy-dynamic", "bot2.caddy")));

        Assert.Contains("auto_https disable_redirects", caddyfile);
        Assert.Contains("http://84.247.182.209", caddyfile);
        Assert.Contains("respond 404", caddyfile);
        Assert.Contains("header_up X-Forwarded-For {remote_host}", caddyfile);
        Assert.Contains("bot1.mapa-svietidiel.sk", caddyfile);
        Assert.Contains("import /etc/caddy/dynamic/*.caddy", caddyfile);
        Assert.Contains("reverse_proxy gymbeam-shifts-bot-1:8080", caddyfile);
        Assert.Contains("bot2.mapa-svietidiel.sk", caddyfile);
        Assert.Contains("reverse_proxy gymbeam-shifts-bot-2:8080", caddyfile);
    }

    [Fact]
    public void GitIgnore_ProtectsInstanceSecretsAndMutableConfigs()
    {
        string gitIgnore = ReadRootFile(".gitignore");

        Assert.Contains("instances/*/.env", gitIgnore);
        Assert.Contains("instances/*/appconfig.json", gitIgnore);
        Assert.Contains("instances/*/runtime-data/", gitIgnore);
    }

    [Fact]
    public void DeployScript_ReloadsCaddyAfterConfigurationChanges()
    {
        string deployScript = ReadRootFile(Path.Combine("scripts", "deploy.sh"));

        Assert.Contains("caddy reload", deployScript);
        Assert.Contains("--config /etc/caddy/Caddyfile", deployScript);
        Assert.Contains("--address unix//run/caddy-admin/admin.sock", deployScript);
    }

    [Fact]
    public void Ci_BuildsBothImagesAndRunsExplicitTestAndConfigurationGates()
    {
        string workflow = ReadRootFile(Path.Combine(".github", "workflows", "ci.yml"));

        Assert.Contains("docker compose build gymbeam-bot-1 gymbeam-admin-manager", workflow);
        Assert.Contains("Unit tests", workflow);
        Assert.Contains("Integration tests", workflow);
        Assert.Contains("Security tests", workflow);
        Assert.Contains("docker compose config --quiet", workflow);
        Assert.Contains("caddy validate --config /etc/caddy/Caddyfile", workflow);
        Assert.Contains("scripts/managed-bot-deploy.sh", workflow);
        Assert.Contains("bash scripts/tests/deploy-managed-bots.sh", workflow);
        Assert.Contains("actions/checkout@fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09", workflow);
        Assert.Contains("actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1", workflow);
        Assert.Contains("appleboy/ssh-action@7eaf76671a0d7eec5d98ee897acda4f968735a17", workflow);
        Assert.Contains("permissions:\n  contents: read", workflow.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.DoesNotContain("GYMBEAM_AUTH_PASSWORD", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("GYMBEAM_TELEGRAM_BOT_TOKEN", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_PreservesRuntimeBotsAndCreatesRollbackSnapshot()
    {
        string deploy = ReadRootFile(Path.Combine("scripts", "deploy.sh"));
        string rollback = ReadRootFile(Path.Combine("scripts", "rollback.sh"));

        Assert.DoesNotContain("--remove-orphans", deploy, StringComparison.Ordinal);
        Assert.DoesNotContain("docker compose down", deploy, StringComparison.Ordinal);
        Assert.Contains("docker compose build gymbeam-bot-1 gymbeam-admin-manager", deploy);
        Assert.Contains("snapshot_managed_bots", deploy);
        Assert.Contains("recreate_managed_bots", deploy);
        Assert.Contains("remove_managed_bot_backups", deploy);
        Assert.Contains("runtime/deployments", deploy);
        Assert.Contains("admin-manager-data.tar", deploy);
        Assert.Contains("caddy-routes.tar", deploy);
        Assert.Contains("git show \"${DEPLOY_PREVIOUS_COMMIT}:docker-compose.yml\"", deploy);
        Assert.Contains("stat -c '%g' /var/run/docker.sock", deploy);
        Assert.Contains("stat -c '%g' /var/run/docker.sock", rollback);
        Assert.Contains("caddy validate --config /etc/caddy/Caddyfile", deploy);
        Assert.Contains("trap rollback_on_error ERR", deploy);
        Assert.Contains("scripts/rollback.sh", deploy);
        Assert.Contains("--project-directory", rollback);
        Assert.Contains("runtime/caddy-dynamic", rollback);
        Assert.DoesNotContain("docker container prune", deploy, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_DiscoversAndRecreatesEveryManagedBotByLabels()
    {
        string deploy = ReadRootFile(Path.Combine("scripts", "deploy.sh"));
        string helper = ReadRootFile(Path.Combine("scripts", "managed-bot-deploy.sh"));
        string deploymentTest = ReadRootFile(Path.Combine("scripts", "tests", "deploy-managed-bots.sh"));

        Assert.DoesNotContain("BOT_INSTANCES=(bot1 bot2)", deploy);
        Assert.Contains("--filter label=com.gymbeam.managed=true", helper);
        Assert.Contains("--filter label=com.gymbeam.role=bot", helper);
        Assert.Contains("gymbeam-shifts-bot:latest", helper);
        Assert.Contains("docker inspect", helper);
        Assert.Contains("docker rename", helper);
        Assert.Contains("docker exec \"$name\" curl --fail", helper);
        Assert.Contains("wait_for_managed_bot_healthy", helper);
        Assert.Contains("bot-ihor", deploymentTest);
        Assert.Contains("bot-andriana", deploymentTest);
        Assert.Contains("IHOR_NEW_ID", deploymentTest);
        Assert.Contains("ANDRIANA_NEW_ID", deploymentTest);
    }

    [Fact]
    public void RuntimeStateIsGitIgnoredButMigrationTemplatesAreTracked()
    {
        string gitIgnore = ReadRootFile(".gitignore");
        string compose = ReadRootFile("docker-compose.yml");

        Assert.Contains("runtime/caddy-dynamic/", gitIgnore);
        Assert.Contains("runtime/deployments/", gitIgnore);
        Assert.Contains("runtime/caddy/", gitIgnore);
        Assert.Contains("admin_manager_data:/app/data", compose);
        Assert.Contains("./runtime/caddy-dynamic:/etc/caddy/dynamic", compose);
        Assert.Contains("./runtime/caddy/Caddyfile:/etc/caddy/Caddyfile:ro", compose);
        Assert.True(File.Exists(Path.Combine(
            TestPathHelper.GetWorkspaceRoot(), "deploy", "examples", "caddy-dynamic", "bot1.caddy")));
        Assert.True(File.Exists(Path.Combine(
            TestPathHelper.GetWorkspaceRoot(), "deploy", "examples", "caddy-dynamic", "bot2.caddy")));
    }

    [Fact]
    public void ManagerContainerIsHardenedAndAllPersistentServicesRestartSafely()
    {
        string compose = ReadRootFile("docker-compose.yml");

        Assert.Contains("read_only: true", compose);
        Assert.Contains("no-new-privileges:true", compose);
        Assert.Contains("cap_drop:", compose);
        Assert.Contains("- ALL", compose);
        Assert.Contains("/tmp:size=64m,mode=1777", compose);
        Assert.Contains("restart: unless-stopped", compose);
        Assert.Contains("admin_manager_data:/app/data", compose);
        Assert.DoesNotContain("admin_manager_data:/app/data:ro", compose);
        Assert.DoesNotContain("/var/run/docker.sock:/var/run/docker.sock:ro", compose);
    }

    private static string ReadRootFile(string name)
    {
        return File.ReadAllText(Path.Combine(TestPathHelper.GetWorkspaceRoot(), name));
    }
}
