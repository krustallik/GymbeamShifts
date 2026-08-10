using System.Text;
using GymBeam.AdminManager.Security;

namespace GymBeam.AdminManager.Tests;

public class SessionTokenServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateAndValidate_WithValidToken_ReturnsPayload()
    {
        var clock = new ManualTimeProvider(Now);
        var service = new SessionTokenService(
            Encoding.UTF8.GetBytes("a-test-signing-key-that-is-at-least-32-bytes"),
            TimeSpan.FromHours(1),
            clock);

        string token = service.Create("manager-admin", out SessionTokenPayload created);
        bool valid = service.TryValidate(token, out SessionTokenPayload validated);

        Assert.True(valid);
        Assert.Equal(created.SessionId, validated.SessionId);
        Assert.Equal("manager-admin", validated.Username);
        Assert.Equal(Now.AddHours(1), validated.ExpiresAtUtc);
    }

    [Fact]
    public void TryValidate_WhenPayloadOrSignatureIsTampered_ReturnsFalse()
    {
        var service = CreateService(new ManualTimeProvider(Now));
        string token = service.Create("manager-admin", out _);
        string[] parts = token.Split('.');

        Assert.False(service.TryValidate($"{parts[0]}x.{parts[1]}", out _));
        Assert.False(service.TryValidate($"{parts[0]}.{parts[1]}x", out _));
    }

    [Fact]
    public void TryValidate_WhenExpired_ReturnsFalse()
    {
        var clock = new ManualTimeProvider(Now);
        var service = CreateService(clock);
        string token = service.Create("manager-admin", out _);

        clock.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));

        Assert.False(service.TryValidate(token, out _));
    }

    private static SessionTokenService CreateService(TimeProvider clock)
    {
        return new SessionTokenService(
            Encoding.UTF8.GetBytes("a-test-signing-key-that-is-at-least-32-bytes"),
            TimeSpan.FromHours(1),
            clock);
    }
}
