namespace GymBeam.AdminManager.Storage;

internal static class StoragePermissions
{
    private const UnixFileMode OwnerDirectoryMode = UnixFileMode.UserRead
        | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead
        | UnixFileMode.UserWrite;

    public static void EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, OwnerDirectoryMode);
        }
    }

    public static void EnsureFile(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, OwnerFileMode);
        }
    }
}
