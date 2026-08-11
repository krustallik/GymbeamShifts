namespace GymBeam.AdminManager.Provisioning;

public interface IBotResidualDataCleaner
{
    Task<ResourceResult> RemoveAsync(string botId, CancellationToken cancellationToken = default);
}

public sealed class BotResidualDataCleaner(string storageRoot) : IBotResidualDataCleaner
{
    private readonly string _storageRoot = Path.GetFullPath(storageRoot);

    public Task<ResourceResult> RemoveAsync(string botId, CancellationToken cancellationToken = default)
    {
        if (!IsBotId(botId)) return Task.FromResult(ResourceResult.Failure("identity_mismatch"));

        try
        {
            foreach (string category in new[] { "backups", "credential-backups" })
            {
                cancellationToken.ThrowIfCancellationRequested();
                string parent = Path.Combine(_storageRoot, category);
                string path = Path.Combine(parent, botId);
                if (!IsDirectChild(path, parent) || !Directory.Exists(path)) continue;
                var directory = new DirectoryInfo(path);
                if (IsLink(directory)
                    || directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(IsLink))
                {
                    return Task.FromResult(ResourceResult.Failure("unsafe_symlink"));
                }

                Directory.Delete(path, recursive: true);
            }

            string transactionPath = Path.Combine(_storageRoot, "credential-transactions", $"{botId}.json");
            if (File.Exists(transactionPath))
            {
                var file = new FileInfo(transactionPath);
                if (IsLink(file)) return Task.FromResult(ResourceResult.Failure("unsafe_symlink"));
                File.Delete(transactionPath);
            }

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

    private static bool IsDirectChild(string path, string parent) => string.Equals(
        Path.GetDirectoryName(Path.GetFullPath(path)),
        Path.GetFullPath(parent),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsBotId(string value) => value.Length is >= 1 and <= 64
        && value[0] is >= 'a' and <= 'z'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
}
