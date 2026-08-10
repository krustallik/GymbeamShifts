using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GymBeam.AdminManager.Security;

public sealed class CsrfTokenService
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);
    private const int MaximumTokenLength = 2048;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly byte[] _signingKey;
    private readonly TimeProvider _timeProvider;

    public CsrfTokenService(ReadOnlySpan<byte> signingKey, TimeProvider timeProvider)
    {
        if (signingKey.Length < 32)
        {
            throw new ArgumentException("CSRF signing key must contain at least 32 bytes.", nameof(signingKey));
        }

        _signingKey = signingKey.ToArray();
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public string Create(string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        var payload = new CsrfPayload(
            Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            HashContext(context),
            _timeProvider.GetUtcNow().Add(TokenLifetime).ToUnixTimeSeconds());
        string payloadPart = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
        return $"{payloadPart}.{Base64UrlEncode(Sign(payloadPart))}";
    }

    public bool Validate(string token, string context)
    {
        if (string.IsNullOrWhiteSpace(token)
            || string.IsNullOrWhiteSpace(context)
            || token.Length > MaximumTokenLength)
        {
            return false;
        }

        string[] parts = token.Split('.');
        if (parts.Length != 2
            || !TryBase64UrlDecode(parts[1], out byte[] actualSignature))
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
            CsrfPayload? payload = JsonSerializer.Deserialize<CsrfPayload>(payloadBytes, JsonOptions);
            if (payload is null
                || payload.ExpiresAtUnix <= _timeProvider.GetUtcNow().ToUnixTimeSeconds())
            {
                return false;
            }

            byte[] actualContextHash = Convert.FromHexString(payload.ContextHash);
            byte[] expectedContextHash = Convert.FromHexString(HashContext(context));
            return actualContextHash.Length == expectedContextHash.Length
                && CryptographicOperations.FixedTimeEquals(actualContextHash, expectedContextHash);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return false;
        }
    }

    private string HashContext(string context)
    {
        return Convert.ToHexString(HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(context)));
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

    private sealed record CsrfPayload(string Nonce, string ContextHash, long ExpiresAtUnix);
}
