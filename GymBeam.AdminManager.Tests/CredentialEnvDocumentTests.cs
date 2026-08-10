using GymBeam.AdminManager.Credentials;

namespace GymBeam.AdminManager.Tests;

public class CredentialEnvDocumentTests
{
    [Fact]
    public void Apply_PreservesUnknownSettingsAndRoundTripsSpecialCredentialValues()
    {
        const string original = "# keep comment\nGYMBEAM_ADMIN_PORT=8080\nGYMBEAM_AUTH_LOGIN=old\n";
        var updates = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CredentialKeys.GymBeamLogin] = "name=value with spaces \"quoted\" Привіт",
            [CredentialKeys.GymBeamPassword] = "back\\slash='value' = ok",
            [CredentialKeys.TelegramToken] = "123456:token=value with space",
            [CredentialKeys.TelegramChatId] = "-100 123",
            [CredentialKeys.BotAdminUser] = "адмін user",
            [CredentialKeys.BotAdminPassword] = "p=a s s \"word\"",
            [CredentialKeys.BotAdminTokenSecret] = "секрет=значення"
        };

        CredentialEnvDocument document = CredentialEnvDocument.Parse(original);
        string serialized = document.Apply(updates).Serialize();
        CredentialEnvDocument reparsed = CredentialEnvDocument.Parse(serialized);

        Assert.Contains("# keep comment", serialized);
        Assert.Contains("GYMBEAM_ADMIN_PORT=8080", serialized);
        foreach ((string key, string expected) in updates)
        {
            Assert.Equal(expected, reparsed.GetValue(key));
        }
    }

    [Fact]
    public void Parse_DuplicateManagedCredentialKeyFailsClosed()
    {
        const string content = "GYMBEAM_AUTH_LOGIN=first\nGYMBEAM_AUTH_LOGIN=second\n";

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => CredentialEnvDocument.Parse(content));

        Assert.DoesNotContain("first", exception.Message);
        Assert.DoesNotContain("second", exception.Message);
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("carriage\rreturn")]
    [InlineData("nul\0value")]
    public void Apply_RejectsValuesThatCannotBeEnvironmentVariables(string value)
    {
        CredentialEnvDocument document = CredentialEnvDocument.Parse("GYMBEAM_AUTH_LOGIN=old\n");

        Assert.Throws<ArgumentException>(() => document.Apply(
            new Dictionary<string, string> { [CredentialKeys.GymBeamLogin] = value }));
    }
}
