using GymBeam.AdminManager.Credentials;
using GymBeam.AdminManager.Storage;

namespace GymBeam.AdminManager.Provisioning;

public sealed class SafeInstanceProvisioner
{
    private readonly string _root;
    private readonly string _templatePath;

    public SafeInstanceProvisioner(string instancesRoot, string templatePath)
    {
        _root = Path.GetFullPath(instancesRoot);
        _templatePath = Path.GetFullPath(templatePath);
    }

    public async Task<ResourceResult> CreateAsync(
        ProvisioningSpec spec,
        ProvisionBotRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!HasDerivedIdentity(spec) || !string.Equals(request.BotId, spec.BotId, StringComparison.Ordinal))
        {
            return ResourceResult.Failure("identity_mismatch");
        }

        if (!Directory.Exists(_root) || IsLink(new DirectoryInfo(_root)))
        {
            return ResourceResult.Failure("unsafe_instances_root");
        }

        string instancePath = Path.Combine(_root, spec.BotId);
        if (!IsDirectChild(instancePath))
        {
            return ResourceResult.Failure("identity_mismatch");
        }

        if (Directory.Exists(instancePath) || File.Exists(instancePath))
        {
            return ResourceResult.Failure("instance_exists");
        }

        try
        {
            Directory.CreateDirectory(instancePath);
            if (IsLink(new DirectoryInfo(instancePath)))
            {
                return ResourceResult.Failure("unsafe_symlink");
            }

            StoragePermissions.EnsureDirectory(instancePath);
            string runtimePath = Path.Combine(instancePath, "runtime-data");
            Directory.CreateDirectory(runtimePath);
            StoragePermissions.EnsureDirectory(runtimePath);

            var credentials = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CredentialKeys.BotAdminUser] = request.BotAdminUsername,
                [CredentialKeys.BotAdminPassword] = request.BotAdminPassword,
                [CredentialKeys.BotAdminTokenSecret] = request.BotAdminToken
            };
            string envPath = Path.Combine(instancePath, ".env");
            string serialized = CredentialEnvDocument.Parse(string.Empty).Apply(credentials).Serialize();
            await File.WriteAllTextAsync(envPath, serialized, cancellationToken);
            StoragePermissions.EnsureFile(envPath);

            string appConfigPath = Path.Combine(instancePath, "appconfig.json");
            await using (FileStream source = new(
                _templatePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream destination = new(
                appConfigPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            StoragePermissions.EnsureFile(appConfigPath);
            return ResourceResult.Success();
        }
        catch (UnauthorizedAccessException)
        {
            return ResourceResult.Failure("storage_read_only");
        }
        catch (IOException)
        {
            return ResourceResult.Failure("storage_failure");
        }
    }

    public Task<ResourceResult> RemoveAsync(
        ProvisioningSpec spec,
        CancellationToken cancellationToken = default)
    {
        if (!HasManagedIdentity(spec))
        {
            return Task.FromResult(ResourceResult.Failure("identity_mismatch"));
        }

        string instancePath = Path.Combine(_root, spec.BotId);
        if (!IsDirectChild(instancePath))
        {
            return Task.FromResult(ResourceResult.Failure("identity_mismatch"));
        }

        if (!Directory.Exists(instancePath))
        {
            return Task.FromResult(ResourceResult.Success());
        }

        try
        {
            var root = new DirectoryInfo(instancePath);
            if (IsLink(root) || root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(IsLink))
            {
                return Task.FromResult(ResourceResult.Failure("unsafe_symlink"));
            }

            Directory.Delete(instancePath, recursive: true);
            return Task.FromResult(ResourceResult.Success());
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(ResourceResult.Failure("storage_read_only"));
        }
        catch (IOException)
        {
            return Task.FromResult(ResourceResult.Failure("storage_failure"));
        }
    }

    private bool IsDirectChild(string path) =>
        string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(path)),
            _root,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static bool HasDerivedIdentity(ProvisioningSpec spec) =>
        HasManagedIdentity(spec)
        && string.Equals(spec.InstancePath, spec.BotId, StringComparison.Ordinal)
        && string.Equals(spec.ComposeServiceName, $"gymbeam-bot-{spec.BotId}", StringComparison.Ordinal)
        && string.Equals(spec.ContainerName, $"gymbeam-shifts-{spec.BotId}", StringComparison.Ordinal);

    internal static bool HasManagedIdentity(ProvisioningSpec spec) =>
        IsIdentifier(spec.BotId)
        && string.Equals(spec.InstancePath, spec.BotId, StringComparison.Ordinal)
        && IsSafeDockerName(spec.ComposeServiceName)
        && IsSafeDockerName(spec.ContainerName)
        && spec.ComposeServiceName.StartsWith("gymbeam-bot-", StringComparison.Ordinal)
        && spec.ContainerName.StartsWith("gymbeam-shifts-bot", StringComparison.Ordinal)
        && !string.Equals(spec.BotId, "caddy", StringComparison.Ordinal)
        && !string.Equals(spec.BotId, "manager", StringComparison.Ordinal);

    private static bool IsIdentifier(string value) =>
        value.Length is >= 1 and <= 64
        && value[0] is >= 'a' and <= 'z'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool IsSafeDockerName(string value) => value.Length is >= 1 and <= 128
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
}
