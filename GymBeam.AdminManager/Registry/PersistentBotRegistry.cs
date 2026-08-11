using System.Collections.Concurrent;
using System.Text.Json;
using GymBeam.AdminManager.Storage;

namespace GymBeam.AdminManager.Registry;

public sealed class PersistentBotRegistry : IManagedBotRegistryMutations
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _fileLock;

    public PersistentBotRegistry(string path, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider;
        _fileLock = FileLocks.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<IReadOnlyList<ManagedBot>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            RegistryDocument document = await ReadAsync(cancellationToken);
            return document.Bots
                .OrderBy(bot => bot.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<ManagedBot?> TryImportAsync(
        ExistingBotDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            RegistryDocument document = await ReadAsync(cancellationToken);
            if (document.Bots.Any(bot =>
                string.Equals(bot.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            var bot = new ManagedBot(
                descriptor.Id,
                descriptor.DisplayName,
                descriptor.ComposeServiceName,
                descriptor.ContainerName,
                Path.GetFullPath(descriptor.InstancePath),
                Enabled: true,
                RegistrationSource: "import",
                CreatedAtUtc: now,
                UpdatedAtUtc: now);
            document.Bots.Add(bot);
            await WriteAsync(document, cancellationToken);
            return bot;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<ManagedBot?> TryImportOrMigrateAsync(
        ExistingBotDescriptor descriptor,
        string publicHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicHost);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            RegistryDocument document = await ReadAsync(cancellationToken);
            ManagedBot? existing = document.Bots.SingleOrDefault(bot =>
                string.Equals(bot.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                DateTimeOffset now = _timeProvider.GetUtcNow();
                var imported = new ManagedBot(
                    descriptor.Id,
                    descriptor.DisplayName,
                    descriptor.ComposeServiceName,
                    descriptor.ContainerName,
                    Path.GetFullPath(descriptor.InstancePath),
                    Enabled: true,
                    RegistrationSource: "import",
                    CreatedAtUtc: now,
                    UpdatedAtUtc: now)
                {
                    LifecycleState = "active",
                    PublicHost = publicHost
                };
                if (document.Bots.Any(bot => HasIdentityConflict(bot, imported)))
                {
                    throw new InvalidOperationException("Imported bot identity conflicts with the registry.");
                }

                document.Bots.Add(imported);
                await WriteAsync(document, cancellationToken);
                return imported;
            }

            if (!string.Equals(existing.RegistrationSource, "import", StringComparison.Ordinal)
                || !string.Equals(existing.ComposeServiceName, descriptor.ComposeServiceName, StringComparison.Ordinal)
                || !string.Equals(existing.ContainerName, descriptor.ContainerName, StringComparison.Ordinal)
                || !string.Equals(
                    Path.GetFullPath(existing.InstancePath),
                    Path.GetFullPath(descriptor.InstancePath),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || string.Equals(existing.PublicHost, publicHost, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            ManagedBot migrated = existing with
            {
                PublicHost = publicHost,
                UpdatedAtUtc = _timeProvider.GetUtcNow()
            };
            int index = document.Bots.IndexOf(existing);
            document.Bots[index] = migrated;
            await WriteAsync(document, cancellationToken);
            return migrated;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task AddActiveAsync(ManagedBot bot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bot);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            RegistryDocument document = await ReadAsync(cancellationToken);
            if (document.Bots.Any(existing => HasIdentityConflict(existing, bot)))
            {
                throw new InvalidOperationException("A bot with the same managed identity already exists.");
            }

            document.Bots.Add(bot);
            await WriteAsync(document, cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task UpdateAsync(ManagedBot bot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bot);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            RegistryDocument document = await ReadAsync(cancellationToken);
            int index = document.Bots.FindIndex(existing =>
                string.Equals(existing.Id, bot.Id, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new KeyNotFoundException("Managed bot was not found.");
            }

            if (document.Bots.Where((_, candidateIndex) => candidateIndex != index)
                .Any(existing => HasIdentityConflict(existing, bot)))
            {
                throw new InvalidOperationException("A bot with the same managed identity already exists.");
            }

            document.Bots[index] = bot;
            await WriteAsync(document, cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task RemoveAsync(string botId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botId);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            RegistryDocument document = await ReadAsync(cancellationToken);
            int removed = document.Bots.RemoveAll(existing =>
                string.Equals(existing.Id, botId, StringComparison.Ordinal));
            if (removed == 0)
            {
                throw new KeyNotFoundException("Managed bot was not found.");
            }

            await WriteAsync(document, cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static bool HasIdentityConflict(ManagedBot left, ManagedBot right) =>
        string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase)
        || string.Equals(left.ContainerName, right.ContainerName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(left.ComposeServiceName, right.ComposeServiceName, StringComparison.OrdinalIgnoreCase)
        || (left.PublicHost is not null
            && right.PublicHost is not null
            && string.Equals(left.PublicHost, right.PublicHost, StringComparison.OrdinalIgnoreCase));

    private async Task<RegistryDocument> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new RegistryDocument();
        }

        StoragePermissions.EnsureFile(_path);

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        RegistryDocument? document = await JsonSerializer.DeserializeAsync<RegistryDocument>(
            stream,
            JsonOptions,
            cancellationToken);
        return document ?? throw new InvalidDataException("Bot registry is empty or invalid.");
    }

    private async Task WriteAsync(RegistryDocument document, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("Bot registry path has no parent directory.");
        }

        StoragePermissions.EnsureDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                StoragePermissions.EnsureFile(temporaryPath);
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed class RegistryDocument
    {
        public int Version { get; init; } = 1;
        public List<ManagedBot> Bots { get; init; } = [];
    }
}

public sealed record ExistingBotDescriptor(
    string Id,
    string DisplayName,
    string ComposeServiceName,
    string ContainerName,
    string InstancePath);
