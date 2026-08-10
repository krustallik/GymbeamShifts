using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GymBeam.AdminManager.Storage;

public sealed class PersistentSessionStore
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

    public PersistentSessionStore(string path, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _path = Path.GetFullPath(path);
        _timeProvider = timeProvider;
        _fileLock = FileLocks.GetOrAdd(_path, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task AddAsync(
        string sessionId,
        string username,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        string sessionIdHash = HashSessionId(sessionId);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            SessionDocument document = await ReadAsync(cancellationToken);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            document.Sessions.RemoveAll(session => session.ExpiresAtUtc <= now);
            document.Sessions.RemoveAll(session =>
                string.Equals(session.SessionIdHash, sessionIdHash, StringComparison.Ordinal));
            document.Sessions.Add(new StoredSession
            {
                SessionIdHash = sessionIdHash,
                Username = username,
                CreatedAtUtc = now,
                ExpiresAtUtc = expiresAtUtc
            });
            await WriteAsync(document, cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<bool> IsActiveAsync(
        string sessionId,
        string username,
        CancellationToken cancellationToken = default)
    {
        string sessionIdHash = HashSessionId(sessionId);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            SessionDocument document = await ReadAsync(cancellationToken);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            return document.Sessions.Any(session =>
                string.Equals(session.SessionIdHash, sessionIdHash, StringComparison.Ordinal)
                && string.Equals(session.Username, username, StringComparison.Ordinal)
                && session.RevokedAtUtc is null
                && session.ExpiresAtUtc > now);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task RevokeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        string sessionIdHash = HashSessionId(sessionId);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            SessionDocument document = await ReadAsync(cancellationToken);
            StoredSession? session = document.Sessions.FirstOrDefault(candidate =>
                string.Equals(candidate.SessionIdHash, sessionIdHash, StringComparison.Ordinal));
            if (session is null || session.RevokedAtUtc is not null)
            {
                return;
            }

            session.RevokedAtUtc = _timeProvider.GetUtcNow();
            await WriteAsync(document, cancellationToken);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<SessionDocument> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new SessionDocument();
        }

        StoragePermissions.EnsureFile(_path);

        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        SessionDocument? document = await JsonSerializer.DeserializeAsync<SessionDocument>(
            stream,
            JsonOptions,
            cancellationToken);
        return document ?? throw new InvalidDataException("Session storage is empty or invalid.");
    }

    private async Task WriteAsync(SessionDocument document, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("Session storage path has no parent directory.");
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

    private static string HashSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
    }

    private sealed class SessionDocument
    {
        public int Version { get; init; } = 1;
        public List<StoredSession> Sessions { get; init; } = [];
    }

    private sealed class StoredSession
    {
        public required string SessionIdHash { get; init; }
        public required string Username { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset ExpiresAtUtc { get; init; }
        public DateTimeOffset? RevokedAtUtc { get; set; }
    }
}
