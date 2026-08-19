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
    public void SignedToken_BecomesInvalid_WhenSignatureIsNotBase64()
    {
        var validateToken = GetStaticMethod("TryValidateToken");
        var args = new object?[] { "payload.!", null };

        var isValid = (bool)validateToken.Invoke(null, args)!;

        Assert.False(isValid);
    }

    [Fact]
    public void SignedToken_BecomesInvalid_WhenPayloadIsNotBase64()
    {
        var validateToken = GetStaticMethod("TryValidateToken");
        var signPayload = GetStaticMethod("SignPayload");
        var encode = GetStaticMethod("Base64UrlEncode");
        const string invalidPayload = "!";
        string signature = (string)encode.Invoke(
            null,
            new object[] { signPayload.Invoke(null, new object[] { invalidPayload })! })!;
        var args = new object?[] { $"{invalidPayload}.{signature}", null };

        var isValid = (bool)validateToken.Invoke(null, args)!;

        Assert.False(isValid);
    }

    [Theory]
    [InlineData("admin|nonce")]
    [InlineData("admin|not-a-number|nonce")]
    [InlineData(" |4102444800|nonce")]
    public void SignedToken_BecomesInvalid_WhenPayloadFieldsAreInvalid(string payload)
    {
        var validateToken = GetStaticMethod("TryValidateToken");
        var signPayload = GetStaticMethod("SignPayload");
        var encode = GetStaticMethod("Base64UrlEncode");
        string payloadPart = (string)encode.Invoke(null, new object[] { Encoding.UTF8.GetBytes(payload) })!;
        string signaturePart = (string)encode.Invoke(
            null,
            new object[] { signPayload.Invoke(null, new object[] { payloadPart })! })!;
        var args = new object?[] { $"{payloadPart}.{signaturePart}", null };

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

    [Fact]
    public void LoginRateLimit_RemovesExpiredAttempts()
    {
        var server = CreateServer();
        var attemptsField = typeof(AdminWebServer).GetField(
            "_loginAttemptsByIp",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var attempts = (Dictionary<string, List<DateTime>>)attemptsField.GetValue(server)!;
        attempts["127.0.0.3"] = new List<DateTime> { DateTime.UtcNow.AddMinutes(-2) };
        var isLimited = GetInstanceMethod("IsLoginRateLimited");

        var limited = (bool)isLimited.Invoke(server, new object[] { "127.0.0.3" })!;

        Assert.False(limited);
        Assert.DoesNotContain("127.0.0.3", attempts.Keys);
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

    [Theory]
    [InlineData(null, 8080)]
    [InlineData("invalid", 8080)]
    [InlineData("9123", 9123)]
    public void GetAdminPort_UsesConfiguredOrDefaultValue(string? value, int expected)
    {
        string? previous = Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_PORT");
        try
        {
            Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_PORT", value);
            Assert.Equal(expected, (int)GetStaticMethod("GetAdminPort").Invoke(null, null)!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_PORT", previous);
        }
    }

    [Theory]
    [InlineData(null, "localhost")]
    [InlineData("   ", "localhost")]
    [InlineData(" 127.0.0.1 ", "127.0.0.1")]
    public void GetAdminHost_TrimsConfiguredOrUsesDefaultValue(string? value, string expected)
    {
        string? previous = Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_HOST");
        try
        {
            Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_HOST", value);
            Assert.Equal(expected, (string)GetStaticMethod("GetAdminHost").Invoke(null, null)!);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GYMBEAM_ADMIN_HOST", previous);
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
            string today = UserTime.Now.ToString("yyyy-MM-dd");
            string yesterday = UserTime.Now.AddDays(-1).ToString("yyyy-MM-dd");
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
    public void ReadTodayLogLines_ReturnsAllTodayLinesWhenWithinLimit()
    {
        string logPath = Path.Combine(Path.GetTempPath(), $"admin-log-{Guid.NewGuid():N}.log");
        Environment.SetEnvironmentVariable("GYMBEAM_LOG_PATH", logPath);
        ResetLoggerPath();

        try
        {
            string today = UserTime.Now.ToString("yyyy-MM-dd");
            File.WriteAllLines(logPath, new[] { $"{today} one", $"{today} two" });

            var lines = (List<string>)GetStaticMethod("ReadTodayLogLines").Invoke(null, new object[] { 10 })!;

            Assert.Equal(2, lines.Count);
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
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
                StartTimesToSkip = new List<string> { "22:00" },
                StartTimesToSkipOnWeekendsAndHolidays = new List<string> { "22:00" },
                FavoriteShiftUsers = new List<string> { "Andrea Pavlíková" },
                TargetShiftDateTimes = new List<string>
                {
                    "2026-08-25T08:00",
                    "2026-08-26T09:30"
                }
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
        Assert.Equal(new[] { "22:00" }, response.StartTimesToSkipOnWeekendsAndHolidays);
        Assert.Equal(new[] { "Andrea Pavlíková" }, response.FavoriteShiftUsers);
        Assert.Equal(
            new[] { "2026-08-25T08:00", "2026-08-26T09:30" },
            response.TargetShiftDateTimes);
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

    [Theory]
    [InlineData(new byte[] { 1 })]
    [InlineData(new byte[] { 1, 2 })]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void Base64UrlEncodeDecode_RoundTripsDifferentPaddingLengths(byte[] original)
    {
        var encode = GetStaticMethod("Base64UrlEncode");
        var decode = GetStaticMethod("Base64UrlDecode");

        string encoded = (string)encode.Invoke(null, new object[] { original })!;
        byte[] decoded = (byte[])decode.Invoke(null, new object[] { encoded })!;

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void BuildAdminHtml_IncludesTakeLunchControlAndApiBinding()
    {
        string html = (string)GetStaticMethod("BuildAdminHtml").Invoke(null, null)!;

        Assert.Contains("id='takeLunch'", html);
        Assert.Contains("rules.takeLunch", html);
        Assert.Contains("takeLunch: document.getElementById('takeLunch').checked", html);
        Assert.Contains("function readInteger", html);
        Assert.Contains("function isValidTime", html);
        Assert.Contains("function isValidDate", html);
        Assert.Contains("function normalizeTargetShiftDateTime", html);
        Assert.Contains("function buildRulesPayload", html);
        Assert.Contains("id='targetShiftDateTimes'", html);
        Assert.Contains("id='startTimeScopeOptions'", html);
        Assert.Contains("function renderStartTimeScopeOptions", html);
        Assert.Contains("startTimesToSkipOnWeekendsAndHolidays", html);
        Assert.Contains("Також вихідні/свята", html);
        Assert.Contains("id='rulesError'", html);
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
