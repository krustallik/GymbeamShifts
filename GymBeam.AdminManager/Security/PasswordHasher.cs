using System.Security.Cryptography;

namespace GymBeam.AdminManager.Security;

public static class PasswordHasher
{
    private const string Algorithm = "pbkdf2-sha256";
    private const int IterationCount = 210_000;
    private const int MaximumAcceptedIterationCount = 1_000_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            IterationCount,
            HashAlgorithmName.SHA256,
            HashSize);

        return string.Join(
            '$',
            Algorithm,
            IterationCount,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public static bool Verify(string password, string encodedHash)
    {
        if (string.IsNullOrEmpty(password) || !TryParse(encodedHash, out ParsedHash parsed))
        {
            return false;
        }

        byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            parsed.Salt,
            parsed.Iterations,
            HashAlgorithmName.SHA256,
            parsed.Hash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, parsed.Hash);
    }

    public static bool IsSupportedHash(string encodedHash)
    {
        return TryParse(encodedHash, out _);
    }

    private static bool TryParse(string encodedHash, out ParsedHash parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(encodedHash))
        {
            return false;
        }

        string[] parts = encodedHash.Split('$');
        if (parts.Length != 4
            || !string.Equals(parts[0], Algorithm, StringComparison.Ordinal)
            || !int.TryParse(parts[1], out int iterations)
            || iterations is < IterationCount or > MaximumAcceptedIterationCount)
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(parts[2]);
            byte[] hash = Convert.FromBase64String(parts[3]);
            if (salt.Length < SaltSize || hash.Length != HashSize)
            {
                return false;
            }

            parsed = new ParsedHash(iterations, salt, hash);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private readonly record struct ParsedHash(int Iterations, byte[] Salt, byte[] Hash);
}
