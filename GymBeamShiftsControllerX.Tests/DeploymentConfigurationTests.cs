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
        Assert.Contains("./instances/bot1/.env", compose);
        Assert.Contains("./instances/bot2/.env", compose);
        Assert.Contains("./instances/bot1/appconfig.json:/app/appconfig.json", compose);
        Assert.Contains("./instances/bot2/appconfig.json:/app/appconfig.json", compose);
        Assert.Contains("./instances/bot1/runtime-data:/app/runtime-data", compose);
        Assert.Contains("./instances/bot2/runtime-data:/app/runtime-data", compose);
        Assert.DoesNotContain("8080:8080", compose);
    }

    [Fact]
    public void Caddy_RoutesEachDomainToItsOwnBot()
    {
        string caddyfile = ReadRootFile("Caddyfile");

        Assert.Contains("http://84.247.182.209", caddyfile);
        Assert.Contains("respond 404", caddyfile);
        Assert.Contains("bot1.mapa-svietidiel.sk", caddyfile);
        Assert.Contains("reverse_proxy gymbeam-bot-1:8080", caddyfile);
        Assert.Contains("bot2.mapa-svietidiel.sk", caddyfile);
        Assert.Contains("reverse_proxy gymbeam-bot-2:8080", caddyfile);
    }

    [Fact]
    public void GitIgnore_ProtectsInstanceSecretsAndMutableConfigs()
    {
        string gitIgnore = ReadRootFile(".gitignore");

        Assert.Contains("instances/*/.env", gitIgnore);
        Assert.Contains("instances/*/appconfig.json", gitIgnore);
        Assert.Contains("instances/*/runtime-data/", gitIgnore);
    }

    private static string ReadRootFile(string name)
    {
        return File.ReadAllText(Path.Combine(TestPathHelper.GetWorkspaceRoot(), name));
    }
}
