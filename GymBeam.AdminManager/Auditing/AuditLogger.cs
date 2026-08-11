using System.Collections.Concurrent;
using System.Text.Json;
using GymBeam.AdminManager.Storage;

namespace GymBeam.AdminManager.Auditing;

public sealed class AuditLogger
{
    private const long MaximumAuditBytes = 5L * 1024 * 1024;
    private const int MaximumAuditFiles = 12;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _fileLock;

    public AuditLogger(string path, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider;
        _fileLock = FileLocks.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task WriteAsync(
        string action,
        string outcome,
        string? actor,
        string? target,
        string? remoteAddress,
        CancellationToken cancellationToken = default)
    {
        var auditEvent = new AuditEvent(
            _timeProvider.GetUtcNow(),
            action,
            outcome,
            actor,
            target,
            remoteAddress);
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(auditEvent, JsonOptions);

        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("Audit path has no parent directory.");
            }

            StoragePermissions.EnsureDirectory(directory);
            RotateIfNeeded(serialized.Length + 1);
            await using var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            StoragePermissions.EnsureFile(_path);
            await stream.WriteAsync(serialized, cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length + incomingBytes <= MaximumAuditBytes)
        {
            return;
        }

        string directory = Path.GetDirectoryName(_path)!;
        string baseName = Path.GetFileNameWithoutExtension(_path);
        string extension = Path.GetExtension(_path);
        string ArchivePath(int index) => Path.Combine(directory, $"{baseName}.{index}{extension}");
        int maximumArchive = MaximumAuditFiles - 1;

        string oldest = ArchivePath(maximumArchive);
        if (File.Exists(oldest)) File.Delete(oldest);

        for (int index = maximumArchive - 1; index >= 1; index--)
        {
            string source = ArchivePath(index);
            if (File.Exists(source)) File.Move(source, ArchivePath(index + 1));
        }

        File.Move(_path, ArchivePath(1));
    }

    private sealed record AuditEvent(
        DateTimeOffset TimestampUtc,
        string Action,
        string Outcome,
        string? Actor,
        string? Target,
        string? RemoteAddress);
}
