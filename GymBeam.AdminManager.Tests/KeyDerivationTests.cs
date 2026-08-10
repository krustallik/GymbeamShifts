using GymBeam.AdminManager.Security;

namespace GymBeam.AdminManager.Tests;

public class KeyDerivationTests
{
    [Fact]
    public void Derive_IsStableAndSeparatesSessionAndCsrfKeys()
    {
        byte[] masterKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

        byte[] sessionKey = SecurityKeyDerivation.Derive(masterKey, "session-signing-v1");
        byte[] repeatedSessionKey = SecurityKeyDerivation.Derive(masterKey, "session-signing-v1");
        byte[] csrfKey = SecurityKeyDerivation.Derive(masterKey, "csrf-signing-v1");

        Assert.Equal(sessionKey, repeatedSessionKey);
        Assert.NotEqual(sessionKey, csrfKey);
        Assert.Equal(32, sessionKey.Length);
        Assert.Equal(32, csrfKey.Length);
    }
}
