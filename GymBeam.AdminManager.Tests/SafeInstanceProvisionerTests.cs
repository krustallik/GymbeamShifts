using GymBeam.AdminManager.Provisioning;

namespace GymBeam.AdminManager.Tests;

public sealed class SafeInstanceProvisionerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"instance-provision-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateAsync_WritesPrivateEnvAndTemplateWithoutLosingSpecialCharacters()
    {
        Directory.CreateDirectory(_root);
        string template = Path.Combine(_root, "template.json");
        await File.WriteAllTextAsync(template, "{\"safe\":true}");
        var provisioner = new SafeInstanceProvisioner(_root, template);
        ProvisioningSpec spec = Spec("bot3");
        var request = new ProvisionBotRequest(
            "bot3", "Bot", "bot3", "name=value", " spaces \"quotes\" Україна ",
            "123:token", "-100123", "admin", "päss word", "token=value");

        ResourceResult result = await provisioner.CreateAsync(spec, request);

        Assert.True(result.Succeeded);
        string env = await File.ReadAllTextAsync(Path.Combine(_root, "bot3", ".env"));
        Assert.Contains("GYMBEAM_AUTH_LOGIN=\"name=value\"", env, StringComparison.Ordinal);
        Assert.Contains("GYMBEAM_AUTH_PASSWORD=\" spaces \\\"quotes\\\" Україна \"", env, StringComparison.Ordinal);
        Assert.Equal("{\"safe\":true}", await File.ReadAllTextAsync(Path.Combine(_root, "bot3", "appconfig.json")));
        Assert.True(Directory.Exists(Path.Combine(_root, "bot3", "runtime-data")));
    }

    [Fact]
    public async Task CreateAsync_ForgedTraversalSpecIsRejectedOutsideRoot()
    {
        Directory.CreateDirectory(_root);
        string template = Path.Combine(_root, "template.json");
        await File.WriteAllTextAsync(template, "{}");
        var provisioner = new SafeInstanceProvisioner(_root, template);
        ProvisioningSpec forged = Spec("../escaped") with { InstancePath = "../escaped" };

        ResourceResult result = await provisioner.CreateAsync(forged, Request("../escaped"));

        Assert.False(result.Succeeded);
        Assert.Equal("identity_mismatch", result.Outcome);
        Assert.False(Directory.Exists(Path.GetFullPath(Path.Combine(_root, "..", "escaped"))));
    }

    [Fact]
    public async Task CreateAsync_ExistingDirectoryIsNeverOverwritten()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bot3"));
        await File.WriteAllTextAsync(Path.Combine(_root, "bot3", "sentinel"), "keep");
        string template = Path.Combine(_root, "template.json");
        await File.WriteAllTextAsync(template, "{}");
        var provisioner = new SafeInstanceProvisioner(_root, template);

        ResourceResult result = await provisioner.CreateAsync(Spec("bot3"), Request("bot3"));

        Assert.False(result.Succeeded);
        Assert.Equal("instance_exists", result.Outcome);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(_root, "bot3", "sentinel")));
    }

    private static ProvisioningSpec Spec(string id) => new(
        id, "Bot", id, $"{id}.example.test", $"gymbeam-bot-{id}",
        $"gymbeam-shifts-{id}", id);

    private static ProvisionBotRequest Request(string id) => new(
        id, "Bot", id, "login", "password", "telegram-token", "chat", "admin", "password", "token");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
