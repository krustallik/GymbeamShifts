using System;
using System.Collections.Generic;
using System.Reflection;
using GymBeamShiftsControllerX.Models;
using GymBeamShiftsControllerX.Services;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

public class AdminWebServerPrivateLogicTests
{
    [Fact]
    public void SignedToken_IsValid_BeforeExpiry()
    {
        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_TOKEN_SECRET", "unit-test-secret");
        var createToken = GetStaticMethod("CreateSignedToken");
        var validateToken = GetStaticMethod("TryValidateToken");

        var token = (string)createToken.Invoke(null, new object[] { "admin" })!;
        var args = new object?[] { token, null };
        var isValid = (bool)validateToken.Invoke(null, args)!;

        Assert.True(isValid);
        Assert.Equal("admin", args[1] as string);
    }

    [Fact]
    public void SignedToken_BecomesInvalid_WhenSignatureTampered()
    {
        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_TOKEN_SECRET", "unit-test-secret");
        var createToken = GetStaticMethod("CreateSignedToken");
        var validateToken = GetStaticMethod("TryValidateToken");

        var token = (string)createToken.Invoke(null, new object[] { "admin" })!;
        token = token + "tamper";

        var args = new object?[] { token, null };
        var isValid = (bool)validateToken.Invoke(null, args)!;

        Assert.False(isValid);
    }

    [Fact]
    public void LoginRateLimit_BlocksAfterThreeAttemptsPerMinute()
    {
        var server = CreateServer();
        var register = GetInstanceMethod("RegisterLoginAttempt");
        var isLimited = GetInstanceMethod("IsLoginRateLimited");
        string ip = "127.0.0.1";

        register.Invoke(server, new object[] { ip });
        register.Invoke(server, new object[] { ip });
        register.Invoke(server, new object[] { ip });

        var limited = (bool)isLimited.Invoke(server, new object[] { ip })!;
        Assert.True(limited);
    }

    [Fact]
    public void LoginRateLimit_DoesNotBlock_WhenUnderThreshold()
    {
        var server = CreateServer();
        var register = GetInstanceMethod("RegisterLoginAttempt");
        var isLimited = GetInstanceMethod("IsLoginRateLimited");
        string ip = "127.0.0.2";

        register.Invoke(server, new object[] { ip });
        register.Invoke(server, new object[] { ip });

        var limited = (bool)isLimited.Invoke(server, new object[] { ip })!;
        Assert.False(limited);
    }

    private static AdminWebServer CreateServer()
    {
        var cfg = new AppConfig
        {
            Auth = new AuthSettings { Login = "x", Password = "y", LoginUrl = "https://example.com" },
            Telegram = new TelegramSettings { BotToken = "t", ChatId = "c" },
            ShiftRules = new ShiftRulesSettings
            {
                IncludedWeekdays = new List<string> { "Monday" },
                StartTimesToSkip = new List<string>(),
                Holidays = new List<string>(),
                ExcludedDates = new List<string>()
            }
        };

        var store = new ShiftRulesStore(cfg.ShiftRules);
        return new AdminWebServer(cfg, store, () => new BotStatusSnapshot { IsRunning = true });
    }

    private static MethodInfo GetStaticMethod(string name)
    {
        return typeof(AdminWebServer).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
               ?? throw new InvalidOperationException($"Method {name} not found.");
    }

    private static MethodInfo GetInstanceMethod(string name)
    {
        return typeof(AdminWebServer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
               ?? throw new InvalidOperationException($"Method {name} not found.");
    }
}
