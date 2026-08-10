using System.Collections.Concurrent;
using System.Text.Json;
using GymBeam.AdminManager.Storage;

namespace GymBeam.AdminManager.Auditing;

public sealed class AuditLogger
{
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

    private sealed record AuditEvent(
        DateTimeOffset TimestampUtc,
        string Action,
        string Outcome,
        string? Actor,
        string? Target,
        string? RemoteAddress);
}
