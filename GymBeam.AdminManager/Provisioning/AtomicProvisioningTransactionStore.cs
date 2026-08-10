using System.Collections.Concurrent;
using System.Text.Json;
using GymBeam.AdminManager.Storage;

namespace GymBeam.AdminManager.Provisioning;

public sealed class AtomicProvisioningTransactionStore : IProvisioningTransactionStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly SemaphoreSlim _fileLock;

    public AtomicProvisioningTransactionStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _fileLock = FileLocks.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<bool> BeginAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            Journal document = await ReadAsync(cancellationToken);
            if (document.Pending.Any(item => string.Equals(item.BotId, spec.BotId, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            document.Pending.Add(spec);
            await WriteAsync(document, cancellationToken);
            return true;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task CompleteAsync(string botId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            Journal document = await ReadAsync(cancellationToken);
            if (document.Pending.RemoveAll(item => string.Equals(item.BotId, botId, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                await WriteAsync(document, cancellationToken);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<IReadOnlyList<ProvisioningSpec>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            return (await ReadAsync(cancellationToken)).Pending.ToArray();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<Journal> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new Journal();
        }

        StoragePermissions.EnsureFile(_path);
        await using var stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<Journal>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Provisioning journal is invalid.");
    }

    private async Task WriteAsync(Journal document, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Provisioning journal has no parent directory.");
        StoragePermissions.EnsureDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                StoragePermissions.EnsureFile(temporaryPath);
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            StoragePermissions.EnsureFile(_path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed class Journal
    {
        public int Version { get; init; } = 1;
        public List<ProvisioningSpec> Pending { get; init; } = [];
    }
}
