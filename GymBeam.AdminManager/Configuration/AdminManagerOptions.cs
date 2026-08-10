using System.Net;

namespace GymBeam.AdminManager.Configuration;

public sealed record AdminManagerOptions(
    IPAddress BindAddress,
    int Port,
    string PublicHost,
    AdminSecurityOptions Security,
    string StoragePath,
    string InstancesPath)
{
    public AdminDockerOptions Docker { get; init; } = new(
        "/var/run/docker.sock",
        TimeSpan.FromSeconds(3));

    public AdminProvisioningOptions Provisioning { get; init; } = new(
        "example.invalid",
        "gymbeam-shifts-bot:latest",
        "gymbeam-internal",
        "/srv/gymbeam/instances",
        "/run/caddy-admin/admin.sock",
        "/etc/caddy/Caddyfile",
        "/etc/caddy/dynamic",
        "/app/templates/bot-appconfig.json",
        128L * 1024 * 1024);
}

public sealed record AdminDockerOptions(string SocketPath, TimeSpan RequestTimeout)
{
    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan HealthPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int MaximumLogResponseBytes { get; init; } = 256 * 1024;
}

public sealed record AdminProvisioningOptions(
    string BaseDomain,
    string DockerImage,
    string DockerNetwork,
    string DockerHostInstancesPath,
    string CaddyAdminSocketPath,
    string CaddyfilePath,
    string CaddyRoutesPath,
    string BotTemplatePath,
    long MinimumFreeDiskBytes);

public sealed record AdminSecurityOptions(
    string Username,
    string PasswordHash,
    ReadOnlyMemory<byte> SessionSigningKey,
    TimeSpan SessionLifetime,
    int LoginMaxAttempts,
    TimeSpan LoginWindow);
