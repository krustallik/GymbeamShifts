using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using GymBeamShiftsControllerX.Models;
using GymBeamShiftsControllerX.Services;
using Xunit;

namespace GymBeamShiftsControllerX.Tests;

[Collection("MutableEnvironment")]
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
        token += "tamper";

        var args = new object?[] { token, null };
        var isValid = (bool)validateToken.Invoke(null, args)!;

        Assert.False(isValid);
    }

    [Fact]
    public void SignedToken_BecomesInvalid_WhenExpired()
    {
        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_TOKEN_SECRET", "unit-test-secret");
        var validateToken = GetStaticMethod("TryValidateToken");
        var signPayload = GetStaticMethod("SignPayload");
        var encode = GetStaticMethod("Base64UrlEncode");

        long expiredAtUnix = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        string payload = $"admin|{expiredAtUnix}|nonce";
        string payloadPart = (string)encode.Invoke(null, new object[] { Encoding.UTF8.GetBytes(payload) })!;
        string signaturePart = (string)encode.Invoke(null, new object[] { signPayload.Invoke(null, new object[] { payloadPart })! })!;
        string token = $"{payloadPart}.{signaturePart}";

        var args = new object?[] { token, null };
        var isValid = (bool)validateToken.Invoke(null, args)!;

        Assert.False(isValid);
    }

    [Fact]
    public void SignedToken_BecomesInvalid_WhenMalformed()
    {
        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_TOKEN_SECRET", "unit-test-secret");
        var validateToken = GetStaticMethod("TryValidateToken");

        var args = new object?[] { "not-a-valid-token", null };
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

    [Theory]
    [InlineData(0, 1)]
    [InlineData(999, 720)]
    [InlineData(100, 100)]
    public void NormalizeShiftMinHoursAhead_ClampsToExpectedRange(int input, int expected)
    {
        var method = GetStaticMethod("NormalizeShiftMinHoursAhead");
        var result = (int)method.Invoke(null, new object[] { input })!;
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(21, 20)]
    [InlineData(4, 4)]
    public void NormalizeImportantShiftNotificationCount_ClampsToExpectedRange(int input, int expected)
    {
        var method = GetStaticMethod("NormalizeImportantShiftNotificationCount");
        var result = (int)method.Invoke(null, new object[] { input })!;
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(700000, 600000)]
    [InlineData(30000, 30000)]
    public void NormalizeImportantShiftNotificationDelay_ClampsToExpectedRange(int input, int expected)
    {
        var method = GetStaticMethod("NormalizeImportantShiftNotificationDelayMilliseconds");
        var result = (int)method.Invoke(null, new object[] { input })!;
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, 200, 200)]
    [InlineData("abc", 200, 200)]
    [InlineData("0", 200, 1)]
    [InlineData("5000", 200, 1000)]
    public void ParseIntOrDefault_UsesFallbackAndClamp(string? input, int fallback, int expected)
    {
        var method = GetStaticMethod("ParseIntOrDefault");
        var result = (int)method.Invoke(null, new object[] { input!, fallback })!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ValidateAdminCredentials_UsesEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_USER", "env-admin");
        Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_PASSWORD", "env-pass");
        var method = GetStaticMethod("ValidateAdminCredentials");

        try
        {
            Assert.True((bool)method.Invoke(null, new object[] { "env-admin", "env-pass" })!);
            Assert.False((bool)method.Invoke(null, new object[] { "env-admin", "wrong" })!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_USER", null);
            Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_PASSWORD", null);
        }
    }

    [Fact]
    public void ReadTodayLogLines_ReturnsEmptyWhenFileMissing()
    {
        Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.log"));
        ResetLoggerPath();

        try
        {
            var method = GetStaticMethod("ReadTodayLogLines");
            var lines = (List<string>)method.Invoke(null, new object[] { 100 })!;
            Assert.Empty(lines);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", null);
            ResetLoggerPath();
        }
    }

    [Fact]
    public void ReadTodayLogLines_FiltersByTodayAndAppliesTailLimit()
    {
        string logPath = Path.Combine(Path.GetTempPath(), $"admin-log-{Guid.NewGuid():N}.log");
        Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", logPath);
        ResetLoggerPath();

        try
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            string yesterday = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
            File.WriteAllLines(logPath, new[]
            {
                $"{yesterday} 09:00:00 - old",
                $"{today} 09:00:00 - one",
                $"{today} 09:01:00 - two",
                $"{today} 09:02:00 - three"
            });

            var method = GetStaticMethod("ReadTodayLogLines");
            var lines = (List<string>)method.Invoke(null, new object[] { 2 })!;

            Assert.Equal(2, lines.Count);
            Assert.Contains("two", lines[0]);
            Assert.Contains("three", lines[1]);
        }
        finally
        {
            if (File.Exists(logPath))
            {
                File.Delete(logPath);
            }

            Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", null);
            ResetLoggerPath();
        }
    }

    [Fact]
    public void BuildShiftRulesApiResponse_IncludesFavoriteShiftUsersAndShiftMinHoursAhead()
    {
        var cfg = new AppConfig
        {
            Timing = new TimingSettings
            {
                ShiftMinHoursAhead = 55,
                WeekendOrHolidayMinHoursAhead = 24,
                ImportantShiftNotificationCount = 6,
                ImportantShiftNotificationDelayMilliseconds = 45000
            },
            ShiftRules = new ShiftRulesSettings
            {
                TakeLunch = true,
                IncludedWeekdays = new List<string> { "Monday" },
                FavoriteShiftUsers = new List<string> { "Andrea Pavlíková" }
            }
        };
        var store = new ShiftRulesStore(cfg.ShiftRules);
        var server = new AdminWebServer(cfg, store, () => new BotStatusSnapshot());
        var method = GetInstanceMethod("BuildShiftRulesApiResponse");
        var response = (ShiftRulesApiResponse)method.Invoke(server, null)!;

        Assert.True(response.TakeLunch);
        Assert.Equal(55, response.ShiftMinHoursAhead);
        Assert.Equal(24, response.WeekendOrHolidayMinHoursAhead);
        Assert.Equal(6, response.ImportantShiftNotificationCount);
        Assert.Equal(45000, response.ImportantShiftNotificationDelayMilliseconds);
        Assert.Equal(new[] { "Andrea Pavlíková" }, response.FavoriteShiftUsers);
    }

    [Fact]
    public void Base64UrlEncodeDecode_RoundTripsBytes()
    {
        var encode = GetStaticMethod("Base64UrlEncode");
        var decode = GetStaticMethod("Base64UrlDecode");
        byte[] original = { 1, 2, 3, 250 };

        string encoded = (string)encode.Invoke(null, new object[] { original })!;
        byte[] decoded = (byte[])decode.Invoke(null, new object[] { encoded })!;

        Assert.Equal(original, decoded);
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
        return ReflectionTestHelper.GetStaticMethod(typeof(AdminWebServer), name);
    }

    private static MethodInfo GetInstanceMethod(string name)
    {
        return ReflectionTestHelper.GetInstanceMethod(typeof(AdminWebServer), name);
    }

    private static void ResetLoggerPath()
    {
        ReflectionTestHelper.SetStaticField(typeof(Logger), "_logFilePath", null);
    }
}
