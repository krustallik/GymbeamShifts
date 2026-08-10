using System.Collections.Concurrent;

namespace GymBeam.AdminManager.Security;

public sealed class LoginRateLimiter
{
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private int _acquisitionCount;

    public LoginRateLimiter(int permitLimit, TimeSpan window, TimeProvider timeProvider)
    {
        if (permitLimit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(permitLimit));
        }

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        _permitLimit = permitLimit;
        _window = window;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public bool TryAcquire(string partitionKey, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        Bucket bucket = _buckets.GetOrAdd(partitionKey, _ => new Bucket(now));
        bool acquired;

        lock (bucket.SyncRoot)
        {
            if (now - bucket.WindowStartedAtUtc >= _window)
            {
                bucket.WindowStartedAtUtc = now;
                bucket.Count = 0;
            }

            acquired = bucket.Count < _permitLimit;
            if (acquired)
            {
                bucket.Count++;
                retryAfter = TimeSpan.Zero;
            }
            else
            {
                retryAfter = _window - (now - bucket.WindowStartedAtUtc);
            }
        }

        if ((Interlocked.Increment(ref _acquisitionCount) & 255) == 0)
        {
            RemoveExpiredBuckets(now);
        }

        return acquired;
    }

    private void RemoveExpiredBuckets(DateTimeOffset now)
    {
        foreach ((string key, Bucket bucket) in _buckets)
        {
            lock (bucket.SyncRoot)
            {
                if (now - bucket.WindowStartedAtUtc >= _window)
                {
                    _buckets.TryRemove(new KeyValuePair<string, Bucket>(key, bucket));
                }
            }
        }
    }

    private sealed class Bucket(DateTimeOffset windowStartedAtUtc)
    {
        public object SyncRoot { get; } = new();
        public DateTimeOffset WindowStartedAtUtc { get; set; } = windowStartedAtUtc;
        public int Count { get; set; }
    }
}
