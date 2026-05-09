using System;
using System.IO;
using GymBeamShiftsControllerX.Config;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

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
}
