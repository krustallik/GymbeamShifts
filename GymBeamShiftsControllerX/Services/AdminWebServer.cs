using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using GymBeamShiftsControllerX.Config;
using GymBeamShiftsControllerX.Models;

namespace GymBeamShiftsControllerX.Services
{
    public class BotStatusSnapshot
    {
        public bool IsRunning { get; set; }
        public long TotalIterations { get; set; }
        public string LastIterationAt { get; set; } = string.Empty;
        public string LastSuccessAt { get; set; } = string.Empty;
        public string LastErrorAt { get; set; } = string.Empty;
        public string LastErrorMessage { get; set; } = string.Empty;
        public string Uptime { get; set; } = string.Empty;
    }

    public class ShiftRulesUpdateRequest
    {
        public bool TakeLunch { get; set; } = false;
        public List<string> IncludedWeekdays { get; set; } = new List<string>();
        public List<string> StartTimesToSkip { get; set; } = new List<string>();
        public List<string> StartTimesToSkipOnWeekendsAndHolidays { get; set; } = new List<string>();
        public List<string> Holidays { get; set; } = new List<string>();
        public List<string> ExcludedDates { get; set; } = new List<string>();
        public List<string> FavoriteShiftUsers { get; set; } = new List<string>();
        public string TargetShiftDateTime { get; set; } = string.Empty;
        public int ShiftMinHoursAhead { get; set; } = 48;
        public int WeekendOrHolidayMinHoursAhead { get; set; } = 28;
        public int ImportantShiftNotificationCount { get; set; } = 4;
        public int ImportantShiftNotificationDelayMilliseconds { get; set; } = 30000;
    }

    public class ShiftRulesApiResponse
    {
        public bool TakeLunch { get; set; }
        public List<string> IncludedWeekdays { get; set; } = new List<string>();
        public List<string> StartTimesToSkip { get; set; } = new List<string>();
        public List<string> StartTimesToSkipOnWeekendsAndHolidays { get; set; } = new List<string>();
        public List<string> Holidays { get; set; } = new List<string>();
        public List<string> ExcludedDates { get; set; } = new List<string>();
        public List<string> FavoriteShiftUsers { get; set; } = new List<string>();
        public string TargetShiftDateTime { get; set; } = string.Empty;
        public int ShiftMinHoursAhead { get; set; }
        public int WeekendOrHolidayMinHoursAhead { get; set; }
        public int ImportantShiftNotificationCount { get; set; }
        public int ImportantShiftNotificationDelayMilliseconds { get; set; }
    }

    public class UserCredentialsUpdateRequest
    {
        public string GymBeamLogin { get; set; } = string.Empty;
        public string GymBeamPassword { get; set; } = string.Empty;
        public string TelegramBotToken { get; set; } = string.Empty;
        public string TelegramChatId { get; set; } = string.Empty;
    }

    public class AdminWebServer
    {
        private const string SessionCookieName = "gb_admin_session";
        private const int SessionLifetimeDays = 30;
        private const int MaxLoginAttemptsPerMinute = 3;
        private static readonly JsonSerializerOptions ApiJsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        private static readonly JsonSerializerOptions RequestJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        private readonly HttpListener _listener = new HttpListener();
        private readonly AppConfig _config;
        private readonly ShiftRulesStore _shiftRulesStore;
        private readonly Func<BotStatusSnapshot> _statusProvider;
        private readonly string _configFileName;
        private readonly Action _credentialsUpdated;
        private readonly IUserCredentialValidator _credentialValidator;
        private readonly object _loginAttemptsLock = new object();
        private readonly Dictionary<string, List<DateTime>> _loginAttemptsByIp = new Dictionary<string, List<DateTime>>();
        private Thread? _serverThread;

        public AdminWebServer(
            AppConfig config,
            ShiftRulesStore shiftRulesStore,
            Func<BotStatusSnapshot> statusProvider,
            string configFileName = AppConstants.ConfigFileName,
            Action? credentialsUpdated = null,
            IUserCredentialValidator? credentialValidator = null)
        {
            _config = config;
            _shiftRulesStore = shiftRulesStore;
            _statusProvider = statusProvider;
            _configFileName = configFileName;
            _credentialsUpdated = credentialsUpdated ?? (() => { });
            _credentialValidator = credentialValidator ?? new UserCredentialValidator();
        }

        public void Start()
        {
            int port = GetAdminPort();
            string host = GetAdminHost();
            string prefix = $"http://{host}:{port}/";

            try
            {
                _listener.Prefixes.Add(prefix);
                _listener.Start();
            }
            catch (HttpListenerException) when (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                _listener.Prefixes.Clear();
                prefix = $"http://localhost:{port}/";
                _listener.Prefixes.Add(prefix);
                _listener.Start();
            }

            _serverThread = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "AdminWebServer"
            };
            _serverThread.Start();

            Logger.Log($"Admin Web started on {prefix}");
        }

        public void Stop()
        {
            if (!_listener.IsListening)
            {
                return;
            }

            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception ex)
            {
                Logger.Log($"Admin Web stop error: {ex.Message}");
            }
        }

        private void ServerLoop()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = _listener.GetContext();
                    HandleRequest(context);
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Admin Web error: {ex.Message}");
                }
            }
        }

        private void HandleRequest(HttpListenerContext context)
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod.ToUpperInvariant();

            if (method == "GET" && path == "/healthz")
            {
                WriteJson(context.Response, 200, new { status = "ok" });
                return;
            }

            if (method == "GET" && path == "/")
            {
                WriteHtml(context.Response, BuildAdminHtml());
                return;
            }

            if (method == "POST" && path == "/api/login")
            {
                HandleLogin(context);
                return;
            }

            if (method == "POST" && path == "/api/logout")
            {
                ExpireSessionCookie(context);
                WriteJson(context.Response, 200, new { ok = true });
                return;
            }

            if (!IsAuthenticated(context.Request))
            {
                WriteJson(context.Response, 401, new { error = "Unauthorized" });
                return;
            }

            if (method == "GET" && path == "/api/status")
            {
                WriteJson(context.Response, 200, _statusProvider());
                return;
            }

            if (method == "GET" && path == "/api/shift-rules")
            {
                WriteJson(context.Response, 200, BuildShiftRulesApiResponse());
                return;
            }

            if (method == "PUT" && path == "/api/shift-rules")
            {
                HandleShiftRulesUpdate(context);
                return;
            }

            if (method == "GET" && path == "/api/user-credentials")
            {
                WriteJson(context.Response, 200, new
                {
                    gymBeamLoginConfigured = !string.IsNullOrWhiteSpace(_config.Auth.Login),
                    gymBeamPasswordConfigured = !string.IsNullOrWhiteSpace(_config.Auth.Password),
                    telegramBotTokenConfigured = !string.IsNullOrWhiteSpace(_config.Telegram.BotToken),
                    telegramChatIdConfigured = !string.IsNullOrWhiteSpace(_config.Telegram.ChatId)
                });
                return;
            }

            if (method == "PUT" && path == "/api/user-credentials")
            {
                HandleUserCredentialsUpdate(context);
                return;
            }

            if (method == "GET" && path == "/api/logs/today")
            {
                int limit = ParseIntOrDefault(context.Request.QueryString["limit"], 200);
                var lines = ReadTodayLogLines(limit);
                WriteJson(context.Response, 200, new { lines });
                return;
            }

            WriteJson(context.Response, 404, new { error = "Not found" });
        }

        private void HandleLogin(HttpListenerContext context)
        {
            string clientIp = GetClientIp(context.Request);
            if (IsLoginRateLimited(clientIp))
            {
                WriteJson(context.Response, 429, new { error = "Too many login attempts. Try again later." });
                return;
            }

            var body = ReadRequestBody(context.Request);
            Dictionary<string, string>? payload;
            try
            {
                payload = JsonSerializer.Deserialize<Dictionary<string, string>>(body, RequestJsonOptions);
            }
            catch (JsonException)
            {
                WriteJson(context.Response, 400, new { error = "Invalid payload" });
                return;
            }

            if (payload == null)
            {
                WriteJson(context.Response, 400, new { error = "Invalid payload" });
                return;
            }

            payload.TryGetValue("username", out var username);
            payload.TryGetValue("password", out var password);
            if (!ValidateAdminCredentials(username ?? string.Empty, password ?? string.Empty))
            {
                RegisterLoginAttempt(clientIp);
                WriteJson(context.Response, 401, new { error = "Invalid credentials" });
                return;
            }

            string token = CreateSignedToken(username ?? string.Empty);
            SetSessionCookie(context, token);
            WriteJson(context.Response, 200, new { ok = true });
        }

        private bool IsLoginRateLimited(string clientIp)
        {
            var now = DateTime.UtcNow;
            lock (_loginAttemptsLock)
            {
                if (!_loginAttemptsByIp.TryGetValue(clientIp, out var attempts))
                {
                    return false;
                }

                attempts.RemoveAll(attempt => (now - attempt) > TimeSpan.FromMinutes(1));
                if (attempts.Count == 0)
                {
                    _loginAttemptsByIp.Remove(clientIp);
                    return false;
                }

                return attempts.Count >= MaxLoginAttemptsPerMinute;
            }
        }

        private void RegisterLoginAttempt(string clientIp)
        {
            var now = DateTime.UtcNow;
            lock (_loginAttemptsLock)
            {
                if (!_loginAttemptsByIp.TryGetValue(clientIp, out var attempts))
                {
                    attempts = new List<DateTime>();
                    _loginAttemptsByIp[clientIp] = attempts;
                }

                attempts.RemoveAll(attempt => (now - attempt) > TimeSpan.FromMinutes(1));
                attempts.Add(now);
            }
        }

        private static string GetClientIp(HttpListenerRequest request)
        {
            string? forwardedFor = request.Headers["X-Forwarded-For"];
            if (!string.IsNullOrWhiteSpace(forwardedFor))
            {
                string firstAddress = forwardedFor.Split(',')[0].Trim();
                if (!string.IsNullOrWhiteSpace(firstAddress))
                {
                    return firstAddress;
                }
            }

            string? realIp = request.Headers["X-Real-IP"];
            if (!string.IsNullOrWhiteSpace(realIp))
            {
                return realIp.Trim();
            }

            return request.RemoteEndPoint?.Address.ToString() ?? "unknown";
        }

        private void HandleShiftRulesUpdate(HttpListenerContext context)
        {
            try
            {
                var body = ReadRequestBody(context.Request);
                var update = JsonSerializer.Deserialize<ShiftRulesUpdateRequest>(body, RequestJsonOptions);
                if (update == null)
                {
                    WriteJson(context.Response, 400, new { error = "Invalid payload" });
                    return;
                }

                if (!IsValidTargetShiftDateTime(update.TargetShiftDateTime))
                {
                    WriteJson(context.Response, 400, new
                    {
                        error = "Дата й час вибраної зміни мають бути у форматі РРРР-ММ-ДД ГГ:ХХ."
                    });
                    return;
                }

                int shiftMinHoursAhead = NormalizeShiftMinHoursAhead(update.ShiftMinHoursAhead);
                int weekendOrHolidayMinHoursAhead = NormalizeShiftMinHoursAhead(update.WeekendOrHolidayMinHoursAhead);
                int importantShiftNotificationCount = NormalizeImportantShiftNotificationCount(update.ImportantShiftNotificationCount);
                int importantShiftNotificationDelayMilliseconds = NormalizeImportantShiftNotificationDelayMilliseconds(
                    update.ImportantShiftNotificationDelayMilliseconds);
                var updatedRules = _shiftRulesStore.Update(update);
                _config.ShiftRules = updatedRules;
                _config.Timing.ShiftMinHoursAhead = shiftMinHoursAhead;
                _config.Timing.WeekendOrHolidayMinHoursAhead = weekendOrHolidayMinHoursAhead;
                _config.Timing.ImportantShiftNotificationCount = importantShiftNotificationCount;
                _config.Timing.ImportantShiftNotificationDelayMilliseconds = importantShiftNotificationDelayMilliseconds;
                ConfigurationLoader.SaveShiftRules(_configFileName, updatedRules);
                ConfigurationLoader.SaveShiftTimingSettings(
                    _configFileName,
                    shiftMinHoursAhead,
                    weekendOrHolidayMinHoursAhead,
                    importantShiftNotificationCount,
                    importantShiftNotificationDelayMilliseconds);

                Logger.Log("ShiftRules updated from admin API.");
                WriteJson(context.Response, 200, new { ok = true });
            }
            catch (Exception ex)
            {
                Logger.Log($"ShiftRules update failed: {ex.Message}");
                WriteJson(context.Response, 500, new { error = "Failed to update ShiftRules" });
            }
        }

        private bool IsAuthenticated(HttpListenerRequest request)
        {
            var cookie = request.Cookies[SessionCookieName];
            if (cookie == null || string.IsNullOrWhiteSpace(cookie.Value))
            {
                return false;
            }

            return TryValidateToken(cookie.Value, out _);
        }

        private static void SetSessionCookie(HttpListenerContext context, string token)
        {
            AppendSessionCookieHeader(
                context,
                token,
                DateTime.UtcNow.AddDays(SessionLifetimeDays),
                maxAgeSeconds: SessionLifetimeDays * 24 * 60 * 60);
        }

        private void HandleUserCredentialsUpdate(HttpListenerContext context)
        {
            try
            {
                UserCredentialsUpdateRequest? update = JsonSerializer.Deserialize<UserCredentialsUpdateRequest>(
                    ReadRequestBody(context.Request), RequestJsonOptions);
                if (update == null)
                {
                    WriteJson(context.Response, 400, new { error = "Неправильні дані" });
                    return;
                }

                string login = MergeCredential(update.GymBeamLogin, _config.Auth.Login);
                string password = MergeCredential(update.GymBeamPassword, _config.Auth.Password);
                string telegramToken = MergeCredential(update.TelegramBotToken, _config.Telegram.BotToken);
                string chatId = MergeCredential(update.TelegramChatId, _config.Telegram.ChatId);
                if (!ValidCredential(login) || !ValidCredential(password)
                    || !ValidCredential(telegramToken) || !ValidCredential(chatId))
                {
                    WriteJson(context.Response, 400, new { error = "Заповніть усі чотири поля коректними значеннями" });
                    return;
                }


                var candidate = new UserCredentialsUpdateRequest
                {
                    GymBeamLogin = login,
                    GymBeamPassword = password,
                    TelegramBotToken = telegramToken,
                    TelegramChatId = chatId
                };
                UserCredentialValidationResult validation = _credentialValidator.Validate(_config, candidate);
                if (!validation.Succeeded)
                {
                    WriteJson(context.Response, 400, new
                    {
                        error = "Перевірка налаштувань не пройдена",
                        validation.TelegramValid,
                        validation.GymBeamValid,
                        validation.TelegramMessage,
                        validation.GymBeamMessage
                    });
                    return;
                }

                ConfigurationLoader.SaveUserCredentials(_configFileName, login, password, telegramToken, chatId);
                _config.Auth.Login = login;
                _config.Auth.Password = password;
                _config.Telegram.BotToken = telegramToken;
                _config.Telegram.ChatId = chatId;
                Logger.Log("User-managed credentials updated from bot admin UI.");
                _credentialsUpdated();
                WriteJson(context.Response, 200, new
                {
                    ok = true,
                    configured = true,
                    validation.TelegramValid,
                    validation.GymBeamValid,
                    validation.TelegramMessage,
                    validation.GymBeamMessage
                });
            }
            catch (Exception ex)
            {
                Logger.Log($"User credential update failed: {ex.Message}");
                WriteJson(context.Response, 500, new { error = "Не вдалося зберегти налаштування" });
            }
        }

        private static string MergeCredential(string? update, string current) =>
            string.IsNullOrWhiteSpace(update) ? current : update.Trim();

        private static bool ValidCredential(string value) =>
            !string.IsNullOrWhiteSpace(value)
            && value.Length <= 4096
            && value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;

        private static void ExpireSessionCookie(HttpListenerContext context)
        {
            AppendSessionCookieHeader(context, string.Empty, DateTime.UnixEpoch, maxAgeSeconds: 0);
        }

        private static void AppendSessionCookieHeader(
            HttpListenerContext context,
            string value,
            DateTime expiresUtc,
            int maxAgeSeconds)
        {
            string header =
                $"{SessionCookieName}={value}; Path=/; Expires={expiresUtc:R}; Max-Age={maxAgeSeconds}; HttpOnly; SameSite=Strict";

            if (IsHttpsRequest(context.Request))
            {
                header += "; Secure";
            }

            context.Response.AppendHeader("Set-Cookie", header);
        }

        private static bool IsHttpsRequest(HttpListenerRequest request)
        {
            return request.IsSecureConnection
                || string.Equals(request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadRequestBody(HttpListenerRequest request)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
            return reader.ReadToEnd();
        }

        private static void WriteHtml(HttpListenerResponse response, string html)
        {
            byte[] data = Encoding.UTF8.GetBytes(html);
            response.StatusCode = 200;
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = data.Length;
            response.OutputStream.Write(data, 0, data.Length);
            response.Close();
        }

        private static void WriteJson(HttpListenerResponse response, int statusCode, object payload)
        {
            string json = JsonSerializer.Serialize(payload, ApiJsonOptions);
            byte[] data = Encoding.UTF8.GetBytes(json);
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = data.Length;
            response.OutputStream.Write(data, 0, data.Length);
            response.Close();
        }

        private ShiftRulesApiResponse BuildShiftRulesApiResponse()
        {
            var rules = _shiftRulesStore.GetSnapshot();
            return new ShiftRulesApiResponse
            {
                TakeLunch = rules.TakeLunch,
                IncludedWeekdays = rules.IncludedWeekdays,
                StartTimesToSkip = rules.StartTimesToSkip,
                StartTimesToSkipOnWeekendsAndHolidays = rules.StartTimesToSkipOnWeekendsAndHolidays,
                Holidays = rules.Holidays,
                ExcludedDates = rules.ExcludedDates,
                FavoriteShiftUsers = rules.FavoriteShiftUsers,
                TargetShiftDateTime = rules.TargetShiftDateTime,
                ShiftMinHoursAhead = _config.Timing.ShiftMinHoursAhead,
                WeekendOrHolidayMinHoursAhead = _config.Timing.WeekendOrHolidayMinHoursAhead,
                ImportantShiftNotificationCount = _config.Timing.ImportantShiftNotificationCount,
                ImportantShiftNotificationDelayMilliseconds = _config.Timing.ImportantShiftNotificationDelayMilliseconds
            };
        }

        private static int NormalizeShiftMinHoursAhead(int value)
        {
            return Math.Min(Math.Max(value, 1), 720);
        }

        private static bool IsValidTargetShiftDateTime(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                || DateTime.TryParseExact(
                    value.Trim(),
                    "yyyy-MM-dd'T'HH:mm",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out _);
        }

        private static int NormalizeImportantShiftNotificationCount(int value)
        {
            return Math.Min(Math.Max(value, 1), 20);
        }

        private static int NormalizeImportantShiftNotificationDelayMilliseconds(int value)
        {
            return Math.Min(Math.Max(value, 0), 600000);
        }

        private static int ParseIntOrDefault(string? value, int fallback)
        {
            if (!int.TryParse(value, out int parsed))
            {
                return fallback;
            }

            return Math.Min(Math.Max(parsed, 1), 1000);
        }

        private static string CreateSignedToken(string username)
        {
            long expiresAtUnix = DateTimeOffset.UtcNow.AddDays(SessionLifetimeDays).ToUnixTimeSeconds();
            string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            string payload = $"{username}|{expiresAtUnix}|{nonce}";
            string payloadPart = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
            string signaturePart = Base64UrlEncode(SignPayload(payloadPart));
            return $"{payloadPart}.{signaturePart}";
        }

        private static bool TryValidateToken(string token, out string username)
        {
            username = string.Empty;
            var parts = token.Split('.');
            if (parts.Length != 2)
            {
                return false;
            }

            string payloadPart = parts[0];
            string signaturePart = parts[1];

            byte[] expectedSignature = SignPayload(payloadPart);
            byte[] actualSignature;
            try
            {
                actualSignature = Base64UrlDecode(signaturePart);
            }
            catch
            {
                return false;
            }

            if (expectedSignature.Length != actualSignature.Length ||
                !CryptographicOperations.FixedTimeEquals(expectedSignature, actualSignature))
            {
                return false;
            }

            string payload;
            try
            {
                payload = Encoding.UTF8.GetString(Base64UrlDecode(payloadPart));
            }
            catch
            {
                return false;
            }

            var payloadParts = payload.Split('|');
            if (payloadParts.Length != 3)
            {
                return false;
            }

            username = payloadParts[0];
            if (!long.TryParse(payloadParts[1], out long expiresAtUnix))
            {
                return false;
            }

            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix);
            if (expiresAt <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(username);
        }

        private static byte[] SignPayload(string payloadPart)
        {
            string secret = Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_TOKEN_SECRET")
                ?? Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_PASSWORD")
                ?? "change-this-token-secret";
            byte[] key = Encoding.UTF8.GetBytes(secret);
            byte[] data = Encoding.UTF8.GetBytes(payloadPart);
            using var hmac = new HMACSHA256(key);
            return hmac.ComputeHash(data);
        }

        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static byte[] Base64UrlDecode(string value)
        {
            string padded = value
                .Replace('-', '+')
                .Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2:
                    padded += "==";
                    break;
                case 3:
                    padded += "=";
                    break;
            }

            return Convert.FromBase64String(padded);
        }

        private static int GetAdminPort()
        {
            string raw = Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_PORT") ?? "8080";
            return int.TryParse(raw, out int port) ? port : 8080;
        }

        private static string GetAdminHost()
        {
            string host = (Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_HOST") ?? "localhost").Trim();
            return string.IsNullOrWhiteSpace(host) ? "localhost" : host;
        }

        private static bool ValidateAdminCredentials(string username, string password)
        {
            string expectedUser = Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_USER") ?? "admin";
            string expectedPass = Environment.GetEnvironmentVariable("GYMBEAM_ADMIN_PASSWORD") ?? "change-me";
            return username == expectedUser && password == expectedPass;
        }

        private static List<string> ReadTodayLogLines(int limit)
        {
            string path = Logger.GetLogFilePath();
            if (!File.Exists(path))
            {
                return new List<string>();
            }

            string prefix = UserTime.Now.ToString("yyyy-MM-dd");
            var lines = File.ReadLines(path)
                .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            if (lines.Count <= limit)
            {
                return lines;
            }

            return lines.Skip(lines.Count - limit).ToList();
        }

        private static string BuildAdminHtml()
        {
            return @"<!doctype html>
<html lang='uk'>
<head>
  <meta charset='utf-8' />
  <meta name='viewport' content='width=device-width,initial-scale=1' />
  <title>Керування ботом GymBeam</title>
  <style>
    body { font-family: Arial, sans-serif; margin: 0; background: #111827; color: #e5e7eb; }
    .container { max-width: 980px; margin: 24px auto; padding: 0 16px; }
    .card { background: #1f2937; border-radius: 10px; padding: 16px; margin-bottom: 16px; }
    h1, h2 { margin-top: 0; }
    label { display:block; margin: 10px 0 4px; }
    input, textarea, button { width:100%; box-sizing:border-box; border-radius: 8px; border: 1px solid #374151; background:#0b1220; color:#e5e7eb; padding:10px; }
    input[type='checkbox'] { width:auto; margin-right:8px; }
    .checkbox-label { display:flex; align-items:center; margin:12px 0; }
    .time-rule-editor { display:grid; grid-template-columns:minmax(140px,.8fr) minmax(220px,1.2fr); gap:10px; align-items:start; }
    .time-scope-panel { min-height:132px; padding:10px; box-sizing:border-box; border:1px solid #374151; border-radius:8px; background:#111827; }
    .time-scope-help-row { display:flex; justify-content:flex-end; }
    .time-scope-empty { color:#9ca3af; font-size:12px; line-height:1.4; }
    .time-scope-list { display:grid; gap:7px; margin-top:10px; }
    .time-scope-row { display:flex; align-items:center; justify-content:space-between; gap:10px; padding:7px 8px; border:1px solid #374151; border-radius:7px; background:#0b1220; }
    .time-scope-row code { color:#f9fafb; font-weight:bold; }
    .time-scope-row label { display:flex; align-items:center; gap:6px; margin:0; color:#cbd5e1; font-size:12px; cursor:pointer; }
    .time-scope-row input { margin:0; }
    button { background: #2563eb; cursor: pointer; margin-top: 10px; }
    button:hover { background:#1d4ed8; }
    .grid { display:grid; gap:16px; grid-template-columns: 1fr 1fr; }
    .hidden { display:none; }
    .validation-error { color:#fca5a5; margin-top:10px; white-space:pre-wrap; }
    pre { white-space: pre-wrap; max-height: 320px; overflow:auto; background:#0b1220; padding:10px; border-radius:8px; }
    .label-row { display:flex; align-items:center; gap:7px; margin:10px 0 4px; }
    .label-row label { margin:0; }
    .help { position:relative; display:inline-grid; place-items:center; width:19px; height:19px; flex:0 0 19px; border:1px solid #60a5fa; border-radius:50%; color:#93c5fd; font-size:12px; font-weight:bold; cursor:help; outline:none; }
    .help::after { content:attr(data-tip); position:fixed; z-index:10000; left:50%; bottom:20px; width:min(420px,calc(100vw - 32px)); max-height:calc(100vh - 40px); overflow-y:auto; box-sizing:border-box; padding:12px 14px; border:1px solid #4b5563; border-radius:10px; background:#030712; color:#e5e7eb; box-shadow:0 14px 45px #000c; font-size:16px; font-weight:normal; line-height:1.5; text-align:left; white-space:normal; overflow-wrap:anywhere; opacity:0; visibility:hidden; transform:translate(-50%,8px); transition:opacity .15s,transform .15s,visibility .15s; pointer-events:none; }
    .help:hover::after,.help:focus::after { opacity:1; visibility:visible; transform:translate(-50%,0); }
    .topbar { display:flex; align-items:center; justify-content:space-between; gap:12px; margin-bottom:16px; }
    .topbar h1 { margin:0; }.topbar button { width:auto; margin:0; }
    dialog { position:relative; width:min(520px,calc(100% - 24px)); padding:0; border:1px solid #374151; border-radius:12px; background:#1f2937; color:#e5e7eb; box-shadow:0 24px 80px #000b; }
    dialog::backdrop { background:#030712cc; }.settings-form{padding:20px}.settings-form h2{margin:0 0 6px}.settings-note{color:#9ca3af;font-size:13px}.dialog-actions{display:flex;gap:10px;margin-top:16px}.dialog-actions button{width:auto;flex:1}.secondary{background:#374151}.configured{color:#86efac}.not-configured{color:#fca5a5}
    .validation-overlay { position:absolute; z-index:9999; inset:0; display:flex; align-items:center; justify-content:center; padding:20px; border-radius:inherit; background:#030712f2; backdrop-filter:blur(5px); cursor:wait; }
    .validation-overlay[hidden] { display:none; }.validation-progress { width:min(430px,100%); padding:30px 24px; border:1px solid #3b82f6; border-radius:16px; background:#111827; box-shadow:0 24px 90px #000; text-align:center; }
    .spinner { width:54px; height:54px; margin:0 auto 20px; border:5px solid #374151; border-top-color:#3b82f6; border-radius:50%; animation:spin .8s linear infinite; }.validation-progress h2{margin:0 0 10px}.validation-progress p{margin:0;color:#cbd5e1;line-height:1.5}.validation-progress .wait-note{margin-top:12px;color:#93c5fd;font-size:13px}@keyframes spin{to{transform:rotate(360deg)}}@media(prefers-reduced-motion:reduce){.spinner{animation-duration:1.8s}}
    @media(max-width:700px){.grid,.time-rule-editor{grid-template-columns:1fr}.container{margin:16px auto}.help::after{left:12px;right:12px;bottom:12px;width:auto;max-height:calc(100vh - 24px);font-size:15px;transform:translateY(8px)}.help:hover::after,.help:focus::after{transform:translateY(0)}}
  </style>
</head>
<body>
  <div class='container'>
    <div class='topbar'><h1>Керування ботом GymBeam</h1><button id='settingsButton' class='secondary hidden' onclick='openSettings()'>Налаштування</button></div>

    <div id='loginCard' class='card'>
      <h2>Вхід</h2>
      <div class='label-row'><label>Ім’я користувача</label><span class='help' tabindex='0' data-tip='Логін для доступу до панелі керування цього бота. Це не логін від сайту GymBeam.'>?</span></div>
      <input id='username' />
      <div class='label-row'><label>Пароль</label><span class='help' tabindex='0' data-tip='Пароль адміністратора цього бота. Він використовується лише для входу в цю панель.'>?</span></div>
      <input id='password' type='password' />
      <button onclick='login()'>Увійти</button>
      <div id='loginError'></div>
    </div>

    <div id='app' class='hidden'>
      <div class='card'>
        <h2>Стан бота <span class='help' tabindex='0' data-tip='Поточний технічний стан бота: чи він працює, кількість перевірок, час останньої успішної операції та остання помилка.'>?</span></h2>
        <pre id='status'>Завантаження...</pre>
      </div>

      <div class='card'>
        <h2>Правила вибору змін</h2>
        <label class='checkbox-label'><input id='takeLunch' type='checkbox' /> Брати обід <span class='help' tabindex='0' data-tip='Визначає, чи бот обиратиме варіант зміни з обідньою перервою під час реєстрації.'>?</span></label>
        <div class='label-row'><label>Мінімум годин до початку зміни</label><span class='help' tabindex='0' data-tip='Бот розглядатиме лише зміни, до початку яких залишилося не менше вказаної кількості годин за місцевим часом Словаччини. Значення є тривалістю, тому додавати різницю часових поясів вручну не потрібно. Допустиме значення: від 1 до 720.'>?</span></div>
        <input id='shiftMinHoursAhead' type='number' min='1' max='720' step='1' required />
        <div class='label-row'><label>Мінімум годин для вихідних і свят</label><span class='help' tabindex='0' data-tip='Окремий мінімальний запас часу для змін у суботу, неділю або дати зі списку свят. Розрахунок виконується за місцевим часом Словаччини; додавати 1 або 2 години вручну не потрібно. Допустиме значення: від 1 до 720 годин.'>?</span></div>
        <input id='weekendOrHolidayMinHoursAhead' type='number' min='1' max='720' step='1' required />
        <div class='label-row'><label>Кількість сповіщень про важливу зміну</label><span class='help' tabindex='0' data-tip='Скільки однакових Telegram-повідомлень надіслати, коли знайдена важлива зміна у вихідний або святковий день. Від 1 до 20.'>?</span></div>
        <input id='importantShiftNotificationCount' type='number' min='1' max='20' step='1' required />
        <div class='label-row'><label>Затримка між важливими сповіщеннями, мс</label><span class='help' tabindex='0' data-tip='Пауза в мілісекундах між повторними Telegram-сповіщеннями про важливу зміну. 1000 мс дорівнює 1 секунді. Від 0 до 600000.'>?</span></div>
        <input id='importantShiftNotificationDelayMilliseconds' type='number' min='0' max='600000' step='1000' required />
        <div class='label-row'><label>Дата й час вибраної зміни</label><span class='help' tabindex='0' data-tip='Необов’язкове поле. Виберіть дату та точний час початку зміни, яку очікуєте. Якщо бот побачить цю зміну без кнопки «Prihlásiť», він повідомлятиме про неї в Telegram на кожній ітерації перевірки. Очистьте поле, щоб вимкнути такі сповіщення.'>?</span></div>
        <input id='targetShiftDateTime' type='datetime-local' step='60' />
        <div class='grid'>
          <div>
            <div class='label-row'><label>Дозволені дні тижня, по одному в рядку</label><span class='help' tabindex='0' data-tip='Дні тижня, у які бот може брати зміни. Вводьте англійські назви Monday–Sunday, оскільки їх очікує система GymBeam.'>?</span></div>
            <textarea id='includedWeekdays' rows='6'></textarea>
            <div class='label-row'><label>Час початку, який треба пропускати</label><span class='help' tabindex='0' data-tip='Кожне значення вводьте з нового рядка у форматі HH:mm. Для кожного часу праворуч з’явиться чекбокс. Якщо його позначити, цей час блокуватиме зміни також у суботу, неділю та святкові дати. Без позначки фільтр діє лише у звичайні будні.'>?</span></div>
            <div class='time-rule-editor'>
              <textarea id='startTimesToSkip' rows='7' oninput='renderStartTimeScopeOptions()'></textarea>
              <div class='time-scope-panel'>
                <div class='time-scope-help-row'><span class='help' tabindex='0' aria-label='Підказка про вихідні та свята' data-tip='Застосовувати у вихідні та свята. Позначте час, якщо його потрібно пропускати також у суботу, неділю та святкові дати.'>?</span></div>
                <div id='startTimeScopeOptions' class='time-scope-list' aria-live='polite'></div>
              </div>
            </div>
            <div class='label-row'><label>Пріоритетні працівники, по одному в рядку</label><span class='help' tabindex='0' data-tip='Якщо на одну дату доступно кілька змін, бот спочатку спробує вибрати зміну працівника з цього списку. Вказуйте ім’я так, як воно показане на сайті.'>?</span></div>
            <textarea id='favoriteShiftUsers' rows='6'></textarea>
          </div>
          <div>
            <div class='label-row'><label>Святкові дати у форматі РРРР-ММ-ДД</label><span class='help' tabindex='0' data-tip='Ці дати бот вважатиме святковими та застосує до них окремий мінімальний запас годин і посилені Telegram-сповіщення.'>?</span></div>
            <textarea id='holidays' rows='6'></textarea>
            <div class='label-row'><label>Виключені дати у форматі РРРР-ММ-ДД</label><span class='help' tabindex='0' data-tip='У ці дати бот повністю ігноруватиме всі доступні зміни та не намагатиметься на них зареєструватися.'>?</span></div>
            <textarea id='excludedDates' rows='6'></textarea>
          </div>
        </div>
        <button onclick='saveRules()'>Зберегти правила</button>
        <div id='rulesError' class='validation-error' role='alert'></div>
      </div>

      <div class='card'>
        <button onclick='logout()'>Вийти</button>
      </div>
    </div>
  </div>
  <dialog id='settingsDialog'>
    <form class='settings-form' onsubmit='saveUserCredentials(event)'>
      <h2>Налаштування підключень <span class='help' tabindex='0' data-tip='Як налаштувати Telegram: 1) відкрийте офіційний чат @BotFather; 2) надішліть /newbot і виконайте його інструкції; 3) скопіюйте створений токен; 4) знайдіть свого нового бота в Telegram і надішліть йому /start; 5) відкрийте в браузері https://api.telegram.org/botВАШ_ТОКЕН/getUpdates, замінивши ВАШ_ТОКЕН на отриманий токен; 6) знайдіть число в result → message → chat → id; 7) вставте токен і Chat ID у поля нижче та натисніть «Зберегти». Нікому не передавайте токен: він дає доступ до керування ботом.'>?</span></h2>
      <p class='settings-note'>Порожнє поле залишає поточне значення без змін. Збережені паролі й токени ніколи не показуються.</p>
      <div id='credentialsState' class='settings-note'></div>
      <div class='label-row'><label>Логін GymBeam</label><span class='help' tabindex='0' data-tip='Логін вашого облікового запису на сайті part-time GymBeam.'>?</span></div><input id='gymBeamLogin' autocomplete='username'>
      <div class='label-row'><label>Пароль GymBeam</label><span class='help' tabindex='0' data-tip='Пароль вашого облікового запису GymBeam. Він потрібен боту для автоматичного входу.'>?</span></div><input id='gymBeamPassword' type='password' autocomplete='new-password'>
      <div class='label-row'><label>Токен Telegram-бота</label><span class='help' tabindex='0' data-tip='Відкрийте офіційного @BotFather у Telegram, надішліть команду /newbot, задайте ім’я та username нового бота. BotFather надішле довгий токен на зразок 123456789:ABC... Скопіюйте його сюди. Потім обов’язково знайдіть створеного бота і надішліть йому /start.'>?</span></div><input id='telegramBotToken' type='password' autocomplete='new-password'>
      <div class='label-row'><label>Telegram Chat ID</label><span class='help' tabindex='0' data-tip='Спочатку знайдіть створеного бота в Telegram і надішліть йому /start. Потім відкрийте в браузері https://api.telegram.org/botВАШ_ТОКЕН/getUpdates, замінивши ВАШ_ТОКЕН на токен від @BotFather. У відповіді знайдіть result → message → chat → id і скопіюйте число з поля id. Якщо result порожній, надішліть боту ще одне повідомлення та оновіть сторінку. Не показуйте нікому URL із токеном.'>?</span></div><input id='telegramChatId' autocomplete='off'>
      <div id='credentialsError' class='validation-error' role='alert'></div>
      <div id='credentialsValidation' class='settings-note' role='status' aria-live='polite'></div>
      <div class='dialog-actions'><button type='button' class='secondary' onclick='closeSettings()'>Скасувати</button><button type='submit'>Зберегти</button></div>
    </form>
    <div id='credentialValidationOverlay' class='validation-overlay' hidden role='alert' aria-live='assertive' aria-busy='true'>
    <div class='validation-progress'>
      <div class='spinner' aria-hidden='true'></div>
      <h2>Перевіряємо ваші дані</h2>
      <p>Надсилаємо тестове повідомлення в Telegram і виконуємо пробний вхід у GymBeam.</p>
      <p class='wait-note'>Будь ласка, не закривайте сторінку. Перевірка може тривати до однієї хвилини.</p>
    </div>
    </div>
  </dialog>

  <script>
    async function api(path, options) {
      const response = await fetch(path, { credentials: 'include', headers: { 'Content-Type': 'application/json' }, ...options });
      if (!response.ok) {
        const payload = await response.json().catch(() => ({}));
        const error = new Error(payload.error || 'Не вдалося виконати запит');
        error.payload = payload;
        throw error;
      }
      return response.json().catch(() => ({}));
    }

    function linesToArray(value) {
      return value.split('\n').map(v => v.trim()).filter(Boolean);
    }

    function arrayToLines(values) {
      return (values || []).join('\n');
    }

    function readInteger(id, label, min, max) {
      const raw = document.getElementById(id).value.trim();
      if (!/^\d+$/.test(raw)) {
        throw new Error(`${label}: введіть ціле число.`);
      }

      const value = Number(raw);
      if (!Number.isSafeInteger(value) || value < min || value > max) {
        throw new Error(`${label}: значення має бути від ${min} до ${max}.`);
      }

      return value;
    }

    function validateList(values, label, predicate, expectedFormat) {
      for (const value of values) {
        if (!predicate(value)) {
          throw new Error(`${label}: неправильне значення '${value}'. Очікується ${expectedFormat}.`);
        }
      }
      return values;
    }

    function isValidTime(value) {
      return /^(?:[01]\d|2[0-3]):[0-5]\d$/.test(value);
    }

    function isValidDate(value) {
      if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) {
        return false;
      }

      const [year, month, day] = value.split('-').map(Number);
      const date = new Date(Date.UTC(year, month - 1, day));
      return date.getUTCFullYear() === year
        && date.getUTCMonth() === month - 1
        && date.getUTCDate() === day;
    }

    function readOptionalDateTimeLocal(id, label) {
      const value = document.getElementById(id).value.trim();
      if (!value) {
        return '';
      }

      const parts = value.split('T');
      if (parts.length !== 2 || !isValidDate(parts[0]) || !isValidTime(parts[1])) {
        throw new Error(`${label}: виберіть правильну дату й час.`);
      }

      return value;
    }

    let startTimesToSkipOnWeekendsAndHolidays = new Set();

    function renderStartTimeScopeOptions() {
      const times = [...new Set(linesToArray(document.getElementById('startTimesToSkip').value))];
      const availableTimes = new Set(times);
      startTimesToSkipOnWeekendsAndHolidays = new Set(
        [...startTimesToSkipOnWeekendsAndHolidays].filter(value => availableTimes.has(value)));

      const container = document.getElementById('startTimeScopeOptions');
      container.replaceChildren();

      if (times.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'time-scope-empty';
        empty.textContent = 'Введіть час зліва — тут автоматично з’являться налаштування.';
        container.appendChild(empty);
        return;
      }

      for (const time of times) {
        const row = document.createElement('div');
        row.className = 'time-scope-row';

        const timeLabel = document.createElement('code');
        timeLabel.textContent = time;

        const checkboxLabel = document.createElement('label');
        const checkbox = document.createElement('input');
        checkbox.type = 'checkbox';
        checkbox.checked = startTimesToSkipOnWeekendsAndHolidays.has(time);
        checkbox.dataset.startTime = time;
        checkbox.addEventListener('change', () => {
          if (checkbox.checked) {
            startTimesToSkipOnWeekendsAndHolidays.add(time);
          } else {
            startTimesToSkipOnWeekendsAndHolidays.delete(time);
          }
        });
        checkboxLabel.append(checkbox, document.createTextNode('Також вихідні/свята'));
        row.append(timeLabel, checkboxLabel);
        container.appendChild(row);
      }
    }

    function buildRulesPayload() {
      const allowedWeekdays = new Set([
        'monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday'
      ]);
      const includedWeekdays = validateList(
        linesToArray(document.getElementById('includedWeekdays').value),
        'Дозволені дні тижня',
        value => allowedWeekdays.has(value.toLowerCase()),
        'назва дня від Monday до Sunday');
      const startTimesToSkip = validateList(
        linesToArray(document.getElementById('startTimesToSkip').value),
        'Час початку, який треба пропускати',
        isValidTime,
        'час у форматі HH:mm (00:00–23:59)');
      const favoriteShiftUsers = validateList(
        linesToArray(document.getElementById('favoriteShiftUsers').value),
        'Пріоритетні працівники',
        value => value.length <= 100,
        'ім’я довжиною до 100 символів');
      const holidays = validateList(
        linesToArray(document.getElementById('holidays').value),
        'Святкові дати',
        isValidDate,
        'реальна дата у форматі РРРР-ММ-ДД');
      const excludedDates = validateList(
        linesToArray(document.getElementById('excludedDates').value),
        'Виключені дати',
        isValidDate,
        'реальна дата у форматі РРРР-ММ-ДД');

      return {
        takeLunch: document.getElementById('takeLunch').checked,
        shiftMinHoursAhead: readInteger('shiftMinHoursAhead', 'Мінімум годин до початку зміни', 1, 720),
        weekendOrHolidayMinHoursAhead: readInteger('weekendOrHolidayMinHoursAhead', 'Мінімум годин для вихідних і свят', 1, 720),
        importantShiftNotificationCount: readInteger('importantShiftNotificationCount', 'Кількість сповіщень про важливу зміну', 1, 20),
        importantShiftNotificationDelayMilliseconds: readInteger('importantShiftNotificationDelayMilliseconds', 'Затримка між важливими сповіщеннями', 0, 600000),
        targetShiftDateTime: readOptionalDateTimeLocal('targetShiftDateTime', 'Дата й час вибраної зміни'),
        includedWeekdays,
        startTimesToSkip,
        startTimesToSkipOnWeekendsAndHolidays: startTimesToSkip.filter(
          value => startTimesToSkipOnWeekendsAndHolidays.has(value)),
        favoriteShiftUsers,
        holidays,
        excludedDates
      };
    }

    async function login() {
      const username = document.getElementById('username').value;
      const password = document.getElementById('password').value;
      try {
        await api('/api/login', { method: 'POST', body: JSON.stringify({ username, password }) });
        document.getElementById('loginCard').classList.add('hidden');
        document.getElementById('app').classList.remove('hidden');
        document.getElementById('settingsButton').classList.remove('hidden');
        await refreshAll();
        const credentials = await api('/api/user-credentials');
        if (!(credentials.gymBeamLoginConfigured && credentials.gymBeamPasswordConfigured
          && credentials.telegramBotTokenConfigured && credentials.telegramChatIdConfigured)) await openSettings();
      } catch (e) {
        document.getElementById('loginError').innerText = e.message;
      }
    }

    async function logout() {
      await api('/api/logout', { method: 'POST' });
      location.reload();
    }

    async function loadStatus() {
      const status = await api('/api/status');
      document.getElementById('status').innerText = JSON.stringify(status, null, 2);
    }

    async function loadRules() {
      const rules = await api('/api/shift-rules');
      document.getElementById('takeLunch').checked = rules.takeLunch ?? false;
      document.getElementById('shiftMinHoursAhead').value = rules.shiftMinHoursAhead ?? 48;
      document.getElementById('weekendOrHolidayMinHoursAhead').value = rules.weekendOrHolidayMinHoursAhead ?? 28;
      document.getElementById('importantShiftNotificationCount').value = rules.importantShiftNotificationCount ?? 4;
      document.getElementById('importantShiftNotificationDelayMilliseconds').value = rules.importantShiftNotificationDelayMilliseconds ?? 30000;
      document.getElementById('targetShiftDateTime').value = rules.targetShiftDateTime ?? '';
      document.getElementById('includedWeekdays').value = arrayToLines(rules.includedWeekdays);
      document.getElementById('startTimesToSkip').value = arrayToLines(rules.startTimesToSkip);
      startTimesToSkipOnWeekendsAndHolidays = new Set(
        rules.startTimesToSkipOnWeekendsAndHolidays ?? []);
      renderStartTimeScopeOptions();
      document.getElementById('favoriteShiftUsers').value = arrayToLines(rules.favoriteShiftUsers);
      document.getElementById('holidays').value = arrayToLines(rules.holidays);
      document.getElementById('excludedDates').value = arrayToLines(rules.excludedDates);
    }

    async function saveRules() {
      const errorElement = document.getElementById('rulesError');
      errorElement.innerText = '';
      try {
        const payload = buildRulesPayload();
        await api('/api/shift-rules', { method: 'PUT', body: JSON.stringify(payload) });
        await loadRules();
        alert('Правила збережено');
      } catch (e) {
        errorElement.innerText = e.message;
      }
    }

    async function openSettings() {
      document.getElementById('credentialsError').innerText = '';
      document.getElementById('credentialsValidation').innerText = '';
      const state = await api('/api/user-credentials');
      const complete = state.gymBeamLoginConfigured && state.gymBeamPasswordConfigured
        && state.telegramBotTokenConfigured && state.telegramChatIdConfigured;
      const stateElement = document.getElementById('credentialsState');
      stateElement.className = complete ? 'settings-note configured' : 'settings-note not-configured';
      stateElement.innerText = complete ? 'Усі підключення налаштовані.' : 'Потрібно заповнити всі чотири параметри.';
      document.getElementById('settingsDialog').showModal();
    }

    let credentialsValidationInProgress = false;
    const settingsDialog = document.getElementById('settingsDialog');
    settingsDialog.addEventListener('cancel', event => {
      if (credentialsValidationInProgress) event.preventDefault();
    });
    function closeSettings() {
      if (!credentialsValidationInProgress) settingsDialog.close();
    }

    async function saveUserCredentials(event) {
      event.preventDefault();
      const error = document.getElementById('credentialsError');
      const overlay = document.getElementById('credentialValidationOverlay');
      const submitButton = event.currentTarget.querySelector('button[type=submit]');
      error.innerText = '';
      credentialsValidationInProgress = true;
      overlay.hidden = false;
      submitButton.disabled = true;
      document.body.setAttribute('aria-busy','true');
      try {
        const result = await api('/api/user-credentials', { method: 'PUT', body: JSON.stringify({
          gymBeamLogin: document.getElementById('gymBeamLogin').value,
          gymBeamPassword: document.getElementById('gymBeamPassword').value,
          telegramBotToken: document.getElementById('telegramBotToken').value,
          telegramChatId: document.getElementById('telegramChatId').value
        }) });
        document.querySelector('#settingsDialog form').reset();
        document.getElementById('credentialsValidation').innerText =
          '✅ Telegram: '+result.telegramMessage+'\n✅ GymBeam: '+result.gymBeamMessage+'\n\nНалаштування збережено. Бот починає роботу.';
      } catch (e) {
        error.innerText = e.message;
        const details = e.payload;
        if(details) document.getElementById('credentialsValidation').innerText =
          (details.telegramValid?'✅':'❌')+' Telegram: '+(details.telegramMessage||'Не перевірено')+'\n'+
          (details.gymBeamValid?'✅':'❌')+' GymBeam: '+(details.gymBeamMessage||'Не перевірено');
      }
      finally {
        overlay.hidden = true;
        credentialsValidationInProgress = false;
        submitButton.disabled = false;
        document.body.removeAttribute('aria-busy');
      }
    }

    async function refreshAll() {
      await Promise.all([loadStatus(), loadRules()]);
      setInterval(loadStatus, 10000);
    }

    async function init() {
      try {
        await api('/api/status');
        document.getElementById('loginCard').classList.add('hidden');
        document.getElementById('app').classList.remove('hidden');
        document.getElementById('settingsButton').classList.remove('hidden');
        await refreshAll();
        const credentials = await api('/api/user-credentials');
        if (!(credentials.gymBeamLoginConfigured && credentials.gymBeamPasswordConfigured
          && credentials.telegramBotTokenConfigured && credentials.telegramChatIdConfigured)) await openSettings();
      } catch {
        document.getElementById('loginCard').classList.remove('hidden');
        document.getElementById('app').classList.add('hidden');
        document.getElementById('settingsButton').classList.add('hidden');
      }
    }

    init();
  </script>
</body>
</html>";
        }
    }
}
