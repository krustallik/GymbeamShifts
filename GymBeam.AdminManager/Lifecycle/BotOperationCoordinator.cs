using System.Collections.Concurrent;

namespace GymBeam.AdminManager.Lifecycle;

public sealed class BotOperationCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<IDisposable?> TryAcquireAsync(
        string botId,
        CancellationToken cancellationToken = default)
    {
        SemaphoreSlim operationLock = _locks.GetOrAdd(botId, static _ => new SemaphoreSlim(1, 1));
        return await operationLock.WaitAsync(0, cancellationToken)
            ? new Lease(operationLock)
            : null;
    }

    private sealed class Lease(SemaphoreSlim operationLock) : IDisposable
    {
        private SemaphoreSlim? _operationLock = operationLock;

        public void Dispose()
        {
            Interlocked.Exchange(ref _operationLock, null)?.Release();
        }
    }
}
