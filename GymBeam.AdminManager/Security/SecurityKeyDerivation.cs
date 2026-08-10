using System.Security.Cryptography;
using System.Text;

namespace GymBeam.AdminManager.Security;

public static class SecurityKeyDerivation
{
    private const string ContextPrefix = "gymbeam-admin-manager:";

    public static byte[] Derive(ReadOnlySpan<byte> masterKey, string purpose)
    {
        if (masterKey.Length < 32)
        {
            throw new ArgumentException("Master key must contain at least 32 bytes.", nameof(masterKey));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return HMACSHA256.HashData(
            masterKey,
            Encoding.UTF8.GetBytes(ContextPrefix + purpose));
    }
}
