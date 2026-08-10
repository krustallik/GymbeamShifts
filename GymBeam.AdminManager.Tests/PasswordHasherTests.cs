using GymBeam.AdminManager.Security;

namespace GymBeam.AdminManager.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_ProducesPbkdf2HashThatDoesNotContainPassword()
    {
        const string password = "correct horse battery staple";

        string encodedHash = PasswordHasher.Hash(password);

        Assert.StartsWith("pbkdf2-sha256$", encodedHash);
        Assert.DoesNotContain(password, encodedHash, StringComparison.Ordinal);
        Assert.True(PasswordHasher.Verify(password, encodedHash));
    }

    [Fact]
    public void Verify_WithWrongPassword_ReturnsFalse()
    {
        string encodedHash = PasswordHasher.Hash("expected password");

        Assert.False(PasswordHasher.Verify("wrong password", encodedHash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain-text-password")]
    [InlineData("pbkdf2-sha256$1$invalid$invalid")]
    [InlineData("pbkdf2-sha1$210000$c2FsdA==$aGFzaA==")]
    public void Verify_WithMalformedOrWeakHash_ReturnsFalse(string encodedHash)
    {
        Assert.False(PasswordHasher.Verify("password", encodedHash));
        Assert.False(PasswordHasher.IsSupportedHash(encodedHash));
    }
}
