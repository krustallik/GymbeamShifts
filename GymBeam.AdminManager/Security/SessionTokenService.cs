using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GymBeam.AdminManager.Security;

public sealed class SessionTokenService
{
    private const int MaximumTokenLength = 2048;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly byte[] _signingKey;
    private readonly TimeSpan _sessionLifetime;
    private readonly TimeProvider _timeProvider;

    public SessionTokenService(
        ReadOnlySpan<byte> signingKey,
        TimeSpan sessionLifetime,
        TimeProvider timeProvider)
    {
        if (signingKey.Length < 32)
        {
            throw new ArgumentException("Session signing key must contain at least 32 bytes.", nameof(signingKey));
        }

        _signingKey = signingKey.ToArray();
        _sessionLifetime = sessionLifetime;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public string Create(string username, out SessionTokenPayload payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        DateTimeOffset issuedAt = _timeProvider.GetUtcNow();
        payload = new SessionTokenPayload(
            Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            username,
            issuedAt,
            issuedAt.Add(_sessionLifetime));
        var serialized = new SerializedPayload(
            payload.SessionId,
            payload.Username,
            payload.IssuedAtUtc.ToUnixTimeSeconds(),
            payload.ExpiresAtUtc.ToUnixTimeSeconds());
        string payloadPart = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(serialized, JsonOptions));
        string signaturePart = Base64UrlEncode(Sign(payloadPart));
        return $"{payloadPart}.{signaturePart}";
    }

    public bool TryValidate(string token, out SessionTokenPayload payload)
    {
        payload = default!;
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaximumTokenLength)
        {
            return false;
        }

        string[] parts = token.Split('.');
        if (parts.Length != 2 || !TryBase64UrlDecode(parts[1], out byte[] actualSignature))
        {
            return false;
        }

        byte[] expectedSignature = Sign(parts[0]);
        if (actualSignature.Length != expectedSignature.Length
            || !CryptographicOperations.FixedTimeEquals(actualSignature, expectedSignature)
            || !TryBase64UrlDecode(parts[0], out byte[] payloadBytes))
        {
            return false;
        }

        try
        {
            SerializedPayload? serialized = JsonSerializer.Deserialize<SerializedPayload>(payloadBytes, JsonOptions);
            if (serialized is null
                || string.IsNullOrWhiteSpace(serialized.SessionId)
                || string.IsNullOrWhiteSpace(serialized.Username))
            {
                return false;
            }

            DateTimeOffset issuedAt = DateTimeOffset.FromUnixTimeSeconds(serialized.IssuedAtUnix);
            DateTimeOffset expiresAt = DateTimeOffset.FromUnixTimeSeconds(serialized.ExpiresAtUnix);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (expiresAt <= now
                || issuedAt > now.AddMinutes(1)
                || expiresAt <= issuedAt
                || expiresAt - issuedAt > _sessionLifetime)
            {
                return false;
            }

            payload = new SessionTokenPayload(serialized.SessionId, serialized.Username, issuedAt, expiresAt);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private byte[] Sign(string payloadPart)
    {
        return HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(payloadPart));
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryBase64UrlDecode(string value, out byte[] decoded)
    {
        decoded = Array.Empty<byte>();
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty
        };

        try
        {
            decoded = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record SerializedPayload(
        string SessionId,
        string Username,
        long IssuedAtUnix,
        long ExpiresAtUnix);
}

public sealed record SessionTokenPayload(
    string SessionId,
    string Username,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);
