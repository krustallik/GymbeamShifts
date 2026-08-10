using GymBeam.AdminManager.Security;

namespace GymBeam.AdminManager.Tests;

public class LoginRateLimiterTests
{
    [Fact]
    public async Task TryAcquire_ConcurrentRequestsNeverExceedLimitAndResetAfterWindow()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var limiter = new LoginRateLimiter(5, TimeSpan.FromMinutes(1), clock);

        bool[] results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ =>
            Task.Run(() => limiter.TryAcquire("127.0.0.1", out TimeSpan _))));

        Assert.Equal(5, results.Count(allowed => allowed));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquire("127.0.0.1", out _));
    }
}
