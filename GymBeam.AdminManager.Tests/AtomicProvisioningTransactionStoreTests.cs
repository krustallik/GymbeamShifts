using GymBeam.AdminManager.Provisioning;

namespace GymBeam.AdminManager.Tests;

public sealed class AtomicProvisioningTransactionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"provision-journal-{Guid.NewGuid():N}");

    [Fact]
    public async Task PendingTransactionPersistsAcrossStoreInstancesWithoutSecrets()
    {
        string path = Path.Combine(_directory, "provisioning.json");
        var first = new AtomicProvisioningTransactionStore(path);
        ProvisioningSpec spec = ProvisioningValidation.Validate(
            new ProvisionBotRequest(
                "bot3", "Bot Three", "bot3", "secret-login", "secret-password",
                "secret-telegram-token", "secret-chat", "secret-admin", "secret-admin-password", "secret-admin-token"),
            "example.test");

        Assert.True(await first.BeginAsync(spec));
        Assert.False(await first.BeginAsync(spec));

        var restarted = new AtomicProvisioningTransactionStore(path);
        ProvisioningSpec pending = Assert.Single(await restarted.GetPendingAsync());
        Assert.Equal(spec, pending);
        string serialized = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("secret-", serialized, StringComparison.Ordinal);

        await restarted.CompleteAsync(spec.BotId);
        Assert.Empty(await first.GetPendingAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
