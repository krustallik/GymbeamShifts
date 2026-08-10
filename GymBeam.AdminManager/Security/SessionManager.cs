using GymBeam.AdminManager.Storage;

namespace GymBeam.AdminManager.Security;

public sealed class SessionManager(
    SessionTokenService tokenService,
    PersistentSessionStore sessionStore)
{
    public async Task<(string Token, SessionTokenPayload Payload)> CreateAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        string token = tokenService.Create(username, out SessionTokenPayload payload);
        await sessionStore.AddAsync(
            payload.SessionId,
            payload.Username,
            payload.ExpiresAtUtc,
            cancellationToken);
        return (token, payload);
    }

    public async Task<SessionTokenPayload?> ValidateAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (token is null || !tokenService.TryValidate(token, out SessionTokenPayload payload))
        {
            return null;
        }

        bool active = await sessionStore.IsActiveAsync(
            payload.SessionId,
            payload.Username,
            cancellationToken);
        return active ? payload : null;
    }

    public Task RevokeAsync(SessionTokenPayload payload, CancellationToken cancellationToken = default)
    {
        return sessionStore.RevokeAsync(payload.SessionId, cancellationToken);
    }
}
