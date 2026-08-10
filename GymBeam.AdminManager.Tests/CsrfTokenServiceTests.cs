using System.Text;
using GymBeam.AdminManager.Security;

namespace GymBeam.AdminManager.Tests;

public class CsrfTokenServiceTests
{
    [Fact]
    public void Validate_RequiresValidSignatureContextAndExpiration()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var service = new CsrfTokenService(
            Encoding.UTF8.GetBytes("a-test-signing-key-that-is-at-least-32-bytes"),
            clock);
        string token = service.Create("session-a");

        Assert.True(service.Validate(token, "session-a"));
        Assert.False(service.Validate(token, "session-b"));
        Assert.False(service.Validate(token + "tampered", "session-a"));

        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.False(service.Validate(token, "session-a"));
    }
}
