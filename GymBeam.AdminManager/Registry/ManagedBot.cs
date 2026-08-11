namespace GymBeam.AdminManager.Registry;

public sealed record ManagedBot(
    string Id,
    string DisplayName,
    string ComposeServiceName,
    string ContainerName,
    string InstancePath,
    bool Enabled,
    string RegistrationSource,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public string LifecycleState { get; init; } = "active";
    public string? PublicHost { get; init; }
}
