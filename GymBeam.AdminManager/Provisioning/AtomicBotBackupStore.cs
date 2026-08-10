using GymBeam.AdminManager.Registry;
using GymBeam.AdminManager.Storage;

namespace GymBeam.AdminManager.Provisioning;

public sealed class AtomicBotBackupStore(
    string instancesRoot,
    string backupsRoot,
    TimeProvider timeProvider,
    long minimumFreeDiskBytes) : IBotBackupStore
{
    private readonly string _instancesRoot = Path.GetFullPath(instancesRoot);
    private readonly string _backupsRoot = Path.GetFullPath(backupsRoot);

    public async Task<BotBackupResult> CreateAsync(
        ManagedBot bot,
        CancellationToken cancellationToken = default)
    {
        if (!IsBotId(bot.Id))
        {
            return new BotBackupResult(false, "identity_mismatch", null);
        }

        string source = Path.Combine(_instancesRoot, bot.Id);
        if (!IsDirectChild(source, _instancesRoot) || !Directory.Exists(source))
        {
            return new BotBackupResult(false, "instance_not_found", null);
        }

        try
        {
            var sourceInfo = new DirectoryInfo(source);
            FileSystemInfo[] entries = sourceInfo.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).ToArray();
            if (IsLink(sourceInfo) || entries.Any(IsLink))
            {
                return new BotBackupResult(false, "unsafe_symlink", null);
            }

            long sourceBytes = entries.OfType<FileInfo>().Sum(file => file.Length);
            Directory.CreateDirectory(_backupsRoot);
            StoragePermissions.EnsureDirectory(_backupsRoot);
            string driveRoot = Path.GetPathRoot(_backupsRoot)!;
            if (new DriveInfo(driveRoot).AvailableFreeSpace < sourceBytes + minimumFreeDiskBytes)
            {
                return new BotBackupResult(false, "insufficient_disk", null);
            }

            string botBackups = Path.Combine(_backupsRoot, bot.Id);
            Directory.CreateDirectory(botBackups);
            StoragePermissions.EnsureDirectory(botBackups);
            string backupId = $"{timeProvider.GetUtcNow():yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            string temporary = Path.Combine(botBackups, $".tmp-{backupId}");
            string destination = Path.Combine(botBackups, backupId);
            Directory.CreateDirectory(temporary);
            StoragePermissions.EnsureDirectory(temporary);
            try
            {
                foreach (DirectoryInfo directory in entries.OfType<DirectoryInfo>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsLink(directory))
                    {
                        throw new InvalidDataException("Instance contains a symbolic link.");
                    }

                    string relative = Path.GetRelativePath(source, directory.FullName);
                    string target = Path.Combine(temporary, relative);
                    Directory.CreateDirectory(target);
                    StoragePermissions.EnsureDirectory(target);
                }

                foreach (FileInfo file in entries.OfType<FileInfo>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsLink(file))
                    {
                        throw new InvalidDataException("Instance contains a symbolic link.");
                    }

                    string relative = Path.GetRelativePath(source, file.FullName);
                    string target = Path.Combine(temporary, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using FileStream input = new(
                        file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using FileStream output = new(
                        target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                        FileOptions.Asynchronous | FileOptions.WriteThrough);
                    await input.CopyToAsync(output, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                    output.Flush(flushToDisk: true);
                    StoragePermissions.EnsureFile(target);
                }

                Directory.Move(temporary, destination);
                return new BotBackupResult(true, "succeeded", backupId);
            }
            finally
            {
                if (Directory.Exists(temporary))
                {
                    Directory.Delete(temporary, recursive: true);
                }
            }
        }
        catch (InvalidDataException)
        {
            return new BotBackupResult(false, "unsafe_symlink", null);
        }
        catch (UnauthorizedAccessException)
        {
            return new BotBackupResult(false, "backup_read_only", null);
        }
        catch (IOException)
        {
            return new BotBackupResult(false, "backup_failed", null);
        }
    }

    private static bool IsDirectChild(string path, string root) => string.Equals(
        Path.GetDirectoryName(Path.GetFullPath(path)),
        root,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsBotId(string value) => value.Length is >= 1 and <= 64
        && value[0] is >= 'a' and <= 'z'
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
}
