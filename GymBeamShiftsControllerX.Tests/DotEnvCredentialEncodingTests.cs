using System.Reflection;
using GymBeamShiftsControllerX.Config;

namespace GymBeamShiftsControllerX.Tests;

[Collection("MutableEnvironment")]
public class DotEnvCredentialEncodingTests
{
    [Theory]
    [InlineData("\"name=value with spaces \\\"quoted\\\" Привіт\"", "name=value with spaces \"quoted\" Привіт")]
    [InlineData("\"back\\\\slash='value' = ok\"", "back\\slash='value' = ok")]
    [InlineData("plain=value", "plain=value")]
    public void DecodeDotEnvValue_RoundTripsManagerEncoding(string encoded, string expected)
    {
        MethodInfo method = typeof(ConfigurationLoader).GetMethod(
            "DecodeDotEnvValue",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        string result = (string)method.Invoke(null, [encoded])!;

        Assert.Equal(expected, result);
    }

    [Fact]
    public void LoadDotEnvIfExists_UsesExplicitMountedCredentialPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"bot-env-{Guid.NewGuid():N}");
        string key = $"GYMBEAM_TEST_{Guid.NewGuid():N}";
        string? previousPath = Environment.GetEnvironmentVariable("GYMBEAM_ENV_PATH");
        try
        {
            File.WriteAllText(path, $"{key}=\"value=with spaces \\\"quotes\\\" Привіт\"\n");
            Environment.SetEnvironmentVariable("GYMBEAM_ENV_PATH", path);
            Environment.SetEnvironmentVariable(key, null);
            MethodInfo method = typeof(ConfigurationLoader).GetMethod(
                "LoadDotEnvIfExists",
                BindingFlags.NonPublic | BindingFlags.Static)!;

            method.Invoke(null, null);

            Assert.Equal(
                "value=with spaces \"quotes\" Привіт",
                Environment.GetEnvironmentVariable(key));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable("GYMBEAM_ENV_PATH", previousPath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
