using System.Net;
using GymBeam.AdminManager.Security;
using Microsoft.Extensions.Configuration;

namespace GymBeam.AdminManager.Configuration;

public static class AdminManagerConfiguration
{
    public const string EnvironmentPrefix = "ADMIN_MANAGER_";

    public static AdminManagerOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var errors = new List<string>();
        string bindAddressValue = GetRequired(configuration, "BIND_ADDRESS", errors);
        string portValue = GetRequired(configuration, "PORT", errors);
        string publicHost = GetRequired(configuration, "PUBLIC_HOST", errors);
        string username = GetRequired(configuration, "ADMIN_USERNAME", errors);
        string passwordHash = GetRequired(configuration, "ADMIN_PASSWORD_HASH", errors);
        string signingKeyValue = GetRequired(configuration, "SESSION_SIGNING_KEY", errors);
        string sessionLifetimeValue = GetRequired(configuration, "SESSION_LIFETIME_MINUTES", errors);
        string loginMaxAttemptsValue = GetRequired(configuration, "LOGIN_MAX_ATTEMPTS", errors);
        string loginWindowValue = GetRequired(configuration, "LOGIN_WINDOW_SECONDS", errors);
        string storagePath = GetRequired(configuration, "STORAGE_PATH", errors);
        string instancesPath = GetRequired(configuration, "INSTANCES_PATH", errors);
        string dockerSocketPath = GetRequired(configuration, "DOCKER_SOCKET_PATH", errors);
        string dockerTimeoutValue = GetRequired(configuration, "DOCKER_TIMEOUT_SECONDS", errors);
        string dockerHealthTimeoutValue = GetRequired(configuration, "DOCKER_HEALTH_TIMEOUT_SECONDS", errors);
        string dockerHealthPollValue = GetRequired(configuration, "DOCKER_HEALTH_POLL_MILLISECONDS", errors);
        string dockerMaxLogBytesValue = GetRequired(configuration, "DOCKER_MAX_LOG_BYTES", errors);
        string baseDomain = GetRequired(configuration, "PROVISIONING_BASE_DOMAIN", errors);
        string dockerImage = GetRequired(configuration, "PROVISIONING_DOCKER_IMAGE", errors);
        string dockerNetwork = GetRequired(configuration, "PROVISIONING_DOCKER_NETWORK", errors);
        string dockerHostInstancesPath = GetRequired(configuration, "PROVISIONING_DOCKER_INSTANCES_PATH", errors);
        string caddyAdminSocketPath = GetRequired(configuration, "PROVISIONING_CADDY_ADMIN_SOCKET_PATH", errors);
        string caddyfilePath = GetRequired(configuration, "PROVISIONING_CADDYFILE_PATH", errors);
        string caddyRoutesPath = GetRequired(configuration, "PROVISIONING_CADDY_ROUTES_PATH", errors);
        string botTemplatePath = GetRequired(configuration, "PROVISIONING_BOT_TEMPLATE_PATH", errors);
        string minimumFreeDiskValue = GetRequired(configuration, "PROVISIONING_MIN_FREE_DISK_BYTES", errors);

        if (!IPAddress.TryParse(bindAddressValue, out IPAddress? bindAddress))
        {
            errors.Add("BIND_ADDRESS must be a valid IPv4 or IPv6 address.");
        }

        if (!int.TryParse(portValue, out int port) || port is < 1 or > 65535)
        {
            errors.Add("PORT must be an integer between 1 and 65535.");
        }

        if (!IsValidHostName(publicHost))
        {
            errors.Add("PUBLIC_HOST must be a host name without a scheme, port, path, query, or fragment.");
        }

        if (!IsValidUsername(username))
        {
            errors.Add("ADMIN_USERNAME must contain 3 to 64 letters, digits, dots, underscores, or hyphens.");
        }

        if (!PasswordHasher.IsSupportedHash(passwordHash))
        {
            errors.Add("ADMIN_PASSWORD_HASH must use the supported PBKDF2-SHA256 format.");
        }

        byte[] signingKey = ParseSigningKey(signingKeyValue, errors);

        if (!int.TryParse(sessionLifetimeValue, out int sessionLifetimeMinutes)
            || sessionLifetimeMinutes is < 5 or > 1440)
        {
            errors.Add("SESSION_LIFETIME_MINUTES must be an integer between 5 and 1440.");
        }

        if (!int.TryParse(loginMaxAttemptsValue, out int loginMaxAttempts)
            || loginMaxAttempts is < 1 or > 20)
        {
            errors.Add("LOGIN_MAX_ATTEMPTS must be an integer between 1 and 20.");
        }

        if (!int.TryParse(loginWindowValue, out int loginWindowSeconds)
            || loginWindowSeconds is < 10 or > 3600)
        {
            errors.Add("LOGIN_WINDOW_SECONDS must be an integer between 10 and 3600.");
        }

        if (!Path.IsPathFullyQualified(storagePath))
        {
            errors.Add("STORAGE_PATH must be an absolute path.");
        }

        if (!Path.IsPathFullyQualified(instancesPath))
        {
            errors.Add("INSTANCES_PATH must be an absolute path.");
        }

        if (!dockerSocketPath.StartsWith("/", StringComparison.Ordinal))
        {
            errors.Add("DOCKER_SOCKET_PATH must be an absolute Unix socket path.");
        }

        if (!int.TryParse(dockerTimeoutValue, out int dockerTimeoutSeconds)
            || dockerTimeoutSeconds is < 1 or > 30)
        {
            errors.Add("DOCKER_TIMEOUT_SECONDS must be an integer between 1 and 30.");
        }

        if (!int.TryParse(dockerHealthTimeoutValue, out int dockerHealthTimeoutSeconds)
            || dockerHealthTimeoutSeconds is < 5 or > 300)
        {
            errors.Add("DOCKER_HEALTH_TIMEOUT_SECONDS must be an integer between 5 and 300.");
        }

        if (!int.TryParse(dockerHealthPollValue, out int dockerHealthPollMilliseconds)
            || dockerHealthPollMilliseconds is < 100 or > 5000)
        {
            errors.Add("DOCKER_HEALTH_POLL_MILLISECONDS must be an integer between 100 and 5000.");
        }

        if (!int.TryParse(dockerMaxLogBytesValue, out int dockerMaxLogBytes)
            || dockerMaxLogBytes is < 4096 or > 1048576)
        {
            errors.Add("DOCKER_MAX_LOG_BYTES must be an integer between 4096 and 1048576.");
        }

        if (!IsValidHostName(baseDomain) || !baseDomain.Contains('.'))
        {
            errors.Add("PROVISIONING_BASE_DOMAIN must be a valid DNS domain.");
        }

        if (!IsSafeDockerIdentity(dockerImage, allowSlashAndColon: true))
        {
            errors.Add("PROVISIONING_DOCKER_IMAGE contains unsupported characters.");
        }

        if (!IsSafeDockerIdentity(dockerNetwork, allowSlashAndColon: false))
        {
            errors.Add("PROVISIONING_DOCKER_NETWORK contains unsupported characters.");
        }

        if (!dockerHostInstancesPath.StartsWith("/", StringComparison.Ordinal)
            || dockerHostInstancesPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            errors.Add("PROVISIONING_DOCKER_INSTANCES_PATH must be a normalized absolute host path.");
        }

        ValidateAbsolutePath(caddyAdminSocketPath, "PROVISIONING_CADDY_ADMIN_SOCKET_PATH", errors);
        ValidateAbsolutePath(caddyfilePath, "PROVISIONING_CADDYFILE_PATH", errors);
        ValidateAbsolutePath(caddyRoutesPath, "PROVISIONING_CADDY_ROUTES_PATH", errors);
        ValidateAbsolutePath(botTemplatePath, "PROVISIONING_BOT_TEMPLATE_PATH", errors);
        if (!long.TryParse(minimumFreeDiskValue, out long minimumFreeDiskBytes)
            || minimumFreeDiskBytes is < 16_777_216 or > 10_737_418_240)
        {
            errors.Add("PROVISIONING_MIN_FREE_DISK_BYTES must be between 16777216 and 10737418240.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Invalid Admin Manager configuration: {string.Join(" ", errors)}");
        }

        var security = new AdminSecurityOptions(
            username,
            passwordHash,
            signingKey,
            TimeSpan.FromMinutes(sessionLifetimeMinutes),
            loginMaxAttempts,
            TimeSpan.FromSeconds(loginWindowSeconds));
        return new AdminManagerOptions(
            bindAddress!,
            port,
            publicHost,
            security,
            Path.GetFullPath(storagePath),
            Path.GetFullPath(instancesPath))
        {
            Docker = new AdminDockerOptions(
                dockerSocketPath,
                TimeSpan.FromSeconds(dockerTimeoutSeconds))
            {
                HealthTimeout = TimeSpan.FromSeconds(dockerHealthTimeoutSeconds),
                HealthPollInterval = TimeSpan.FromMilliseconds(dockerHealthPollMilliseconds),
                MaximumLogResponseBytes = dockerMaxLogBytes
            },
            Provisioning = new AdminProvisioningOptions(
                baseDomain,
                dockerImage,
                dockerNetwork,
                dockerHostInstancesPath,
                Path.GetFullPath(caddyAdminSocketPath),
                Path.GetFullPath(caddyfilePath),
                Path.GetFullPath(caddyRoutesPath),
                Path.GetFullPath(botTemplatePath),
                minimumFreeDiskBytes)
        };
    }

    private static string GetRequired(
        IConfiguration configuration,
        string key,
        ICollection<string> errors)
    {
        string value = configuration[key]?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            errors.Add($"{key} is required.");
        }

        return value;
    }

    private static bool IsValidHostName(string value)
    {
        return value.Length > 0
            && !value.Contains(':')
            && !value.Contains('/')
            && !value.Contains('?')
            && !value.Contains('#')
            && Uri.CheckHostName(value) != UriHostNameType.Unknown;
    }

    private static bool IsValidUsername(string value)
    {
        return value.Length is >= 3 and <= 64
            && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or '-');
    }

    private static bool IsSafeDockerIdentity(string value, bool allowSlashAndColon) =>
        value.Length is >= 1 and <= 255
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-'
            || (allowSlashAndColon && character is '/' or ':'));

    private static void ValidateAbsolutePath(string value, string key, ICollection<string> errors)
    {
        if (!Path.IsPathFullyQualified(value))
        {
            errors.Add($"{key} must be an absolute path.");
        }
    }

    private static byte[] ParseSigningKey(string encodedValue, ICollection<string> errors)
    {
        try
        {
            byte[] key = Convert.FromBase64String(encodedValue);
            if (key.Length >= 32)
            {
                return key;
            }
        }
        catch (FormatException)
        {
        }

        errors.Add("SESSION_SIGNING_KEY must be valid base64 containing at least 32 bytes.");
        return Array.Empty<byte>();
    }
}
