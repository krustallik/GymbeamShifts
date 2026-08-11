using System.IO;

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
        Assert.Contains("bash -n scripts/deploy.sh scripts/rollback.sh", workflow);
        Assert.Contains("actions/checkout@11d5960a326750d5838078e36cf38b85af677262", workflow);
        Assert.Contains("actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9", workflow);
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
    public void Deploy_SkipsPermanentlyDeletedBotInstances()
    {
        string deploy = ReadRootFile(Path.Combine("scripts", "deploy.sh"));
        string compose = ReadRootFile("docker-compose.yml");
        string caddyService = compose[compose.IndexOf("  caddy:", StringComparison.Ordinal)..];

        Assert.Contains("if [[ ! -e \"$instance_path\" || ! -f \"${instance_path}/.env\" ]]", deploy);
        Assert.Contains("Skipping deleted ${instance}", deploy);
        Assert.Contains("BOT_SERVICES+=(\"gymbeam-bot-${instance#bot}\")", deploy);
        Assert.Contains("for public_host in \"${BOT_PUBLIC_HOSTS[@]}\"", deploy);
        Assert.DoesNotContain("https://bot1.mapa-svietidiel.sk/healthz", deploy);
        Assert.DoesNotContain("https://bot2.mapa-svietidiel.sk/healthz", deploy);
        Assert.DoesNotContain("gymbeam-bot-1:", caddyService);
        Assert.DoesNotContain("gymbeam-bot-2:", caddyService);
        Assert.Contains("gymbeam-admin-manager:", caddyService);
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
