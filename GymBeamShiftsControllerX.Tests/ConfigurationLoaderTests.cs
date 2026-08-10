using System;
using System.Collections.Generic;
using System.IO;
using GymBeamShiftsControllerX.Config;
using GymBeamShiftsControllerX.Models;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

[Collection("MutableEnvironment")]
public class ConfigurationLoaderTests
{
    [Fact]
    public void Load_UsesEnvironmentOverrides_ForSecrets()
    {
        string fileName = $"appconfig.loader.{Guid.NewGuid():N}.json";
        string path = Path.Combine(TestPathHelper.GetWorkspaceRoot(), fileName);

        Environment.SetEnvironmentVariable("GYMBEAM_AUTH_LOGIN", "env-login");
        Environment.SetEnvironmentVariable("GYMBEAM_AUTH_PASSWORD", "env-password");
        Environment.SetEnvironmentVariable("GYMBEAM_TELEGRAM_BOT_TOKEN", "env-token");
        Environment.SetEnvironmentVariable("GYMBEAM_TELEGRAM_CHAT_ID", "env-chat");

        try
        {
            File.WriteAllText(path, """
{
  "Auth": {
    "LoginUrl": "https://example.com/login",
    "Login": "file-login",
    "Password": "file-password",
    "SuccessUrlContains": "/ok"
  },
  "Telegram": {
    "BotToken": "file-token",
    "ChatId": "file-chat"
  }
}
""");

            var cfg = ConfigurationLoader.Load(fileName);

            Assert.Equal("env-login", cfg.Auth.Login);
            Assert.Equal("env-password", cfg.Auth.Password);
            Assert.Equal("env-token", cfg.Telegram.BotToken);
            Assert.Equal("env-chat", cfg.Telegram.ChatId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GYMBEAM_AUTH_LOGIN", null);
            Environment.SetEnvironmentVariable("GYMBEAM_AUTH_PASSWORD", null);
            Environment.SetEnvironmentVariable("GYMBEAM_TELEGRAM_BOT_TOKEN", null);
            Environment.SetEnvironmentVariable("GYMBEAM_TELEGRAM_CHAT_ID", null);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_Throws_WhenRequiredSecretsMissing()
    {
        string fileName = $"appconfig.loader.{Guid.NewGuid():N}.json";
        string path = Path.Combine(TestPathHelper.GetWorkspaceRoot(), fileName);

        try
        {
            File.WriteAllText(path, """
{
  "Auth": {
    "LoginUrl": "https://example.com/login",
    "Login": "",
    "Password": "",
    "SuccessUrlContains": "/ok"
  },
  "Telegram": {
    "BotToken": "",
    "ChatId": ""
  }
}
""");

            var ex = Assert.Throws<InvalidOperationException>(() => ConfigurationLoader.Load(fileName));
            Assert.Contains("Не задан логин", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_Throws_WhenPasswordMissing()
    {
        AssertMissingSecretThrows("""
{
  "Auth": { "LoginUrl": "https://example.com/login", "Login": "user", "Password": "", "SuccessUrlContains": "/ok" },
  "Telegram": { "BotToken": "token", "ChatId": "chat" }
}
""", "Не задан пароль");
    }

    [Fact]
    public void Load_Throws_WhenBotTokenMissing()
    {
        AssertMissingSecretThrows("""
{
  "Auth": { "LoginUrl": "https://example.com/login", "Login": "user", "Password": "pass", "SuccessUrlContains": "/ok" },
  "Telegram": { "BotToken": "", "ChatId": "chat" }
}
""", "Не задан Telegram BotToken");
    }

    [Fact]
    public void Load_Throws_WhenChatIdMissing()
    {
        AssertMissingSecretThrows("""
{
  "Auth": { "LoginUrl": "https://example.com/login", "Login": "user", "Password": "pass", "SuccessUrlContains": "/ok" },
  "Telegram": { "BotToken": "token", "ChatId": "" }
}
""", "Не задан Telegram ChatId");
    }

    [Fact]
    public void Load_Throws_WhenConfigFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => ConfigurationLoader.Load($"missing.{Guid.NewGuid():N}.json"));
    }

    [Fact]
    public void Load_Throws_WhenConfigIsNullJson()
    {
        string fileName = $"appconfig.loader.{Guid.NewGuid():N}.json";
        string path = Path.Combine(TestPathHelper.GetWorkspaceRoot(), fileName);

        try
        {
            File.WriteAllText(path, "null");
            var ex = Assert.Throws<InvalidOperationException>(() => ConfigurationLoader.Load(fileName));
            Assert.Contains("пустой или поврежден", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_DeserializesFavoriteShiftUsers_CaseInsensitively()
    {
        string fileName = $"appconfig.loader.{Guid.NewGuid():N}.json";
        string path = Path.Combine(TestPathHelper.GetWorkspaceRoot(), fileName);

        Environment.SetEnvironmentVariable("GYMBEAM_AUTH_LOGIN", "login");
        Environment.SetEnvironmentVariable("GYMBEAM_AUTH_PASSWORD", "password");
        Environment.SetEnvironmentVariable("GYMBEAM_TELEGRAM_BOT_TOKEN", "token");
        Environment.SetEnvironmentVariable("GYMBEAM_TELEGRAM_CHAT_ID", "chat");

        try
        {
            File.WriteAllText(path, """
{
  "auth": {
    "loginUrl": "https://example.com/login",
    "login": "file-login",
    "password": "password",
    "successUrlContains": "/ok"
  },
  "Telegram": {
    "BotToken": "token",
    "ChatId": "chat"
  },
  "ShiftRules": {
    "FavoriteShiftUsers": ["Andrea Pavlíková", "Lukáš Fialek"]
  }
}
""");

            var cfg = ConfigurationLoader.Load(fileName);

            Assert.Equal("login", cfg.Auth.Login);
            Assert.Equal(new[] { "Andrea Pavlíková", "Lukáš Fialek" }, cfg.ShiftRules.FavoriteShiftUsers);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GYMBEAM_AUTH_LOGIN", null);
            Environment.SetEnvironmentVariable("GYMBEAM_AUTH_PASSWORD", null);
            Environment.SetEnvironmentVariable("GYMBEAM_TELEGRAM_BOT_TOKEN", null);
            Environment.SetEnvironmentVariable("GYMBEAM_TELEGRAM_CHAT_ID", null);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ResolveValue_ResolvesPlaceholderFromEnvironment()
    {
        string key = $"GYMBEAM_RESOLVE_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(key, "resolved-value");
        var method = ReflectionTestHelper.GetStaticMethod(typeof(ConfigurationLoader), "ResolveValue");

        try
        {
            var result = (string)method.Invoke(null, new object[] { $"${{{key}}}" })!;
            Assert.Equal("resolved-value", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public void ResolveValue_ReturnsLiteral_WhenNotPlaceholder()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(ConfigurationLoader), "ResolveValue");
        var result = (string)method.Invoke(null, new object[] { "plain-value" })!;
        Assert.Equal("plain-value", result);
    }

    [Fact]
    public void ResolveValue_Null_ReturnsEmptyString()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(ConfigurationLoader), "ResolveValue");

        var result = (string)method.Invoke(null, new object?[] { null })!;

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void ResolveValue_ReturnsEmpty_WhenPlaceholderEnvironmentVariableMissing()
    {
        string key = $"GYMBEAM_MISSING_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(key, null);
        var method = ReflectionTestHelper.GetStaticMethod(typeof(ConfigurationLoader), "ResolveValue");

        var result = (string)method.Invoke(null, new object[] { $"${{{key}}}" })!;

        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData(null, "fallback")]
    [InlineData("   ", "fallback")]
    [InlineData("configured", "configured")]
    public void GetEnvOrDefault_UsesNonBlankEnvironmentValue(
        string? environmentValue,
        string expected)
    {
        string key = $"GYMBEAM_DEFAULT_{Guid.NewGuid():N}";
        var method = ReflectionTestHelper.GetStaticMethod(typeof(ConfigurationLoader), "GetEnvOrDefault");
        try
        {
            Environment.SetEnvironmentVariable(key, environmentValue);

            var result = (string)method.Invoke(null, new object[] { key, "fallback" })!;

            Assert.Equal(expected, result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public void FindOptionalFilePath_ReturnsNullForMissingFile()
    {
        var method = ReflectionTestHelper.GetStaticMethod(typeof(ConfigurationLoader), "FindOptionalFilePath");

        var result = method.Invoke(null, new object[] { $"missing-{Guid.NewGuid():N}" });

        Assert.Null(result);
    }

    [Fact]
    public void LoadDotEnvIfExists_ParsesQuotedValuesAndComments()
    {
        string envFileName = $".env.test.{Guid.NewGuid():N}";
        string envPath = Path.Combine(TestPathHelper.GetWorkspaceRoot(), envFileName);
        string key = $"GYMBEAM_DOTENV_{Guid.NewGuid():N}";

        try
        {
            File.WriteAllText(envPath, $$"""
# comment
{{key}}="quoted-value"

BADLINE
{{key}}_SECOND=second-value
""");

            var method = ReflectionTestHelper.GetStaticMethod(typeof(ConfigurationLoader), "LoadDotEnvIfExists");
            Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable($"{key}_SECOND", null);

            File.Copy(envPath, Path.Combine(TestPathHelper.GetWorkspaceRoot(), ".env"), overwrite: true);
            method.Invoke(null, null);

            Assert.Equal("quoted-value", Environment.GetEnvironmentVariable(key));
            Assert.Equal("second-value", Environment.GetEnvironmentVariable($"{key}_SECOND"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
            Environment.SetEnvironmentVariable($"{key}_SECOND", null);
            if (File.Exists(envPath)) File.Delete(envPath);
            string workspaceEnv = Path.Combine(TestPathHelper.GetWorkspaceRoot(), ".env");
            if (File.Exists(workspaceEnv)) File.Delete(workspaceEnv);
        }
    }

    [Fact]
    public void Save_RoundTripsAppConfig()
    {
        string fileName = $"appconfig.loader.{Guid.NewGuid():N}.json";
        string path = Path.Combine(TestPathHelper.GetWorkspaceRoot(), fileName);

        try
        {
            var original = new AppConfig
            {
                Auth = new AuthSettings
                {
                    LoginUrl = "https://example.com/login",
                    Login = "login",
                    Password = "password",
                    SuccessUrlContains = "/ok"
                },
                Telegram = new TelegramSettings { BotToken = "token", ChatId = "chat" },
                ShiftRules = new ShiftRulesSettings
                {
                    IncludedWeekdays = new List<string> { "Friday" },
                    FavoriteShiftUsers = new List<string> { "Andrea Pavlíková" }
                }
            };

            File.WriteAllText(path, """
{
  "Auth": {
    "LoginUrl": "https://example.com/login",
    "Login": "login",
    "Password": "password",
    "SuccessUrlContains": "/ok"
  },
  "Telegram": {
    "BotToken": "token",
    "ChatId": "chat"
  },
  "ShiftRules": {
    "IncludedWeekdays": ["Friday"],
    "FavoriteShiftUsers": ["Andrea Pavlíková"]
  }
}
""");

            ConfigurationLoader.Save(fileName, original);
            var loaded = ConfigurationLoader.Load(fileName);

            Assert.Equal(original.ShiftRules.IncludedWeekdays, loaded.ShiftRules.IncludedWeekdays);
            Assert.Equal(original.ShiftRules.FavoriteShiftUsers, loaded.ShiftRules.FavoriteShiftUsers);
            Assert.Equal(original.Auth.LoginUrl, loaded.Auth.LoginUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GYMBEAM_AUTH_LOGIN", null);
            Environment.SetEnvironmentVariable("GYMBEAM_AUTH_PASSWORD", null);
            Environment.SetEnvironmentVariable("GYMBEAM_TELEGRAM_BOT_TOKEN", null);
            Environment.SetEnvironmentVariable("GYMBEAM_TELEGRAM_CHAT_ID", null);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void AssertMissingSecretThrows(string json, string expectedMessagePart)
    {
        string fileName = $"appconfig.loader.{Guid.NewGuid():N}.json";
        string path = Path.Combine(TestPathHelper.GetWorkspaceRoot(), fileName);

        try
        {
            File.WriteAllText(path, json);
            var ex = Assert.Throws<InvalidOperationException>(() => ConfigurationLoader.Load(fileName));
            Assert.Contains(expectedMessagePart, ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
