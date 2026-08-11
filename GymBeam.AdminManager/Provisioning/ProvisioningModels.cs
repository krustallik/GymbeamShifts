using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Provisioning;

public sealed record ProvisionBotRequest(
    string BotId,
    string DisplayName,
    string Subdomain,
    string? GymBeamLogin,
    string? GymBeamPassword,
    string? TelegramToken,
    string? TelegramChatId,
    string BotAdminUsername,
    string BotAdminPassword,
    string BotAdminToken);

public sealed record ProvisioningSpec(
    string BotId,
    string DisplayName,
    string Subdomain,
    string PublicHost,
    string ComposeServiceName,
    string ContainerName,
    string InstancePath)
{
    public ManagedBot ToManagedBot(DateTimeOffset now) => new(
        BotId,
        DisplayName,
        ComposeServiceName,
        ContainerName,
        InstancePath,
        Enabled: true,
        RegistrationSource: "provision",
        CreatedAtUtc: now,
        UpdatedAtUtc: now)
    {
        LifecycleState = "active",
        PublicHost = PublicHost
    };
}

public sealed record ProvisioningResult(string Outcome);

public sealed record ResourceResult(bool Succeeded, string Outcome)
{
    public static ResourceResult Success() => new(true, "succeeded");
    public static ResourceResult Failure(string outcome) => new(false, outcome);
}
