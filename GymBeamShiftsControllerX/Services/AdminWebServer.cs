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
        public List<string> Holidays { get; set; } = new List<string>();
        public List<string> ExcludedDates { get; set; } = new List<string>();
        public List<string> FavoriteShiftUsers { get; set; } = new List<string>();
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
        public List<string> Holidays { get; set; } = new List<string>();
        public List<string> ExcludedDates { get; set; } = new List<string>();
        public List<string> FavoriteShiftUsers { get; set; } = new List<string>();
        public int ShiftMinHoursAhead { get; set; }
        public int WeekendOrHolidayMinHoursAhead { get; set; }
        public int ImportantShiftNotificationCount { get; set; }
        public int ImportantShiftNotificationDelayMilliseconds { get; set; }
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
        private readonly object _loginAttemptsLock = new object();
        private readonly Dictionary<string, List<DateTime>> _loginAttemptsByIp = new Dictionary<string, List<DateTime>>();
        private Thread? _serverThread;

        public AdminWebServer(
            AppConfig config,
            ShiftRulesStore shiftRulesStore,
            Func<BotStatusSnapshot> statusProvider,
            string configFileName = AppConstants.ConfigFileName)
        {
            _config = config;
            _shiftRulesStore = shiftRulesStore;
            _statusProvider = statusProvider;
            _configFileName = configFileName;
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
                Holidays = rules.Holidays,
                ExcludedDates = rules.ExcludedDates,
                FavoriteShiftUsers = rules.FavoriteShiftUsers,
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

            string prefix = DateTime.Now.ToString("yyyy-MM-dd");
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
<html lang='en'>
<head>
  <meta charset='utf-8' />
  <meta name='viewport' content='width=device-width,initial-scale=1' />
  <title>GymBeam Bot Admin</title>
  <style>
    body { font-family: Arial, sans-serif; margin: 0; background: #111827; color: #e5e7eb; }
    .container { max-width: 980px; margin: 24px auto; padding: 0 16px; }
    .card { background: #1f2937; border-radius: 10px; padding: 16px; margin-bottom: 16px; }
    h1, h2 { margin-top: 0; }
    label { display:block; margin: 10px 0 4px; }
    input, textarea, button { width:100%; box-sizing:border-box; border-radius: 8px; border: 1px solid #374151; background:#0b1220; color:#e5e7eb; padding:10px; }
    input[type='checkbox'] { width:auto; margin-right:8px; }
    .checkbox-label { display:flex; align-items:center; margin:12px 0; }
    button { background: #2563eb; cursor: pointer; margin-top: 10px; }
    button:hover { background:#1d4ed8; }
    .grid { display:grid; gap:16px; grid-template-columns: 1fr 1fr; }
    .hidden { display:none; }
    .validation-error { color:#fca5a5; margin-top:10px; white-space:pre-wrap; }
    pre { white-space: pre-wrap; max-height: 320px; overflow:auto; background:#0b1220; padding:10px; border-radius:8px; }
  </style>
</head>
<body>
  <div class='container'>
    <h1>GymBeam Bot Admin</h1>

    <div id='loginCard' class='card'>
      <h2>Login</h2>
      <label>Username</label>
      <input id='username' />
      <label>Password</label>
      <input id='password' type='password' />
      <button onclick='login()'>Login</button>
      <div id='loginError'></div>
    </div>

    <div id='app' class='hidden'>
      <div class='card'>
        <h2>Status</h2>
        <pre id='status'>Loading...</pre>
      </div>

      <div class='card'>
        <h2>Shift Rules</h2>
        <label class='checkbox-label'><input id='takeLunch' type='checkbox' /> Take lunch</label>
        <label>ShiftMinHoursAhead (hours before shift starts)</label>
        <input id='shiftMinHoursAhead' type='number' min='1' max='720' step='1' required />
        <label>WeekendOrHolidayMinHoursAhead</label>
        <input id='weekendOrHolidayMinHoursAhead' type='number' min='1' max='720' step='1' required />
        <label>ImportantShiftNotificationCount</label>
        <input id='importantShiftNotificationCount' type='number' min='1' max='20' step='1' required />
        <label>ImportantShiftNotificationDelayMilliseconds</label>
        <input id='importantShiftNotificationDelayMilliseconds' type='number' min='0' max='600000' step='1000' required />
        <div class='grid'>
          <div>
            <label>IncludedWeekdays (one per line)</label>
            <textarea id='includedWeekdays' rows='6'></textarea>
            <label>StartTimesToSkip (one per line)</label>
            <textarea id='startTimesToSkip' rows='6'></textarea>
            <label>FavoriteShiftUsers (one per line)</label>
            <textarea id='favoriteShiftUsers' rows='6'></textarea>
          </div>
          <div>
            <label>Holidays yyyy-MM-dd (one per line)</label>
            <textarea id='holidays' rows='6'></textarea>
            <label>ExcludedDates yyyy-MM-dd (one per line)</label>
            <textarea id='excludedDates' rows='6'></textarea>
          </div>
        </div>
        <button onclick='saveRules()'>Save ShiftRules</button>
        <div id='rulesError' class='validation-error' role='alert'></div>
      </div>

      <div class='card'>
        <h2>Today Logs</h2>
        <button onclick='loadLogs()'>Refresh Logs</button>
        <pre id='logs'></pre>
      </div>

      <div class='card'>
        <button onclick='logout()'>Logout</button>
      </div>
    </div>
  </div>

  <script>
    async function api(path, options) {
      const response = await fetch(path, { credentials: 'include', headers: { 'Content-Type': 'application/json' }, ...options });
      if (!response.ok) {
        const payload = await response.json().catch(() => ({}));
        throw new Error(payload.error || 'Request failed');
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
        throw new Error(`${label} must be a whole number.`);
      }

      const value = Number(raw);
      if (!Number.isSafeInteger(value) || value < min || value > max) {
        throw new Error(`${label} must be between ${min} and ${max}.`);
      }

      return value;
    }

    function validateList(values, label, predicate, expectedFormat) {
      for (const value of values) {
        if (!predicate(value)) {
          throw new Error(`${label}: invalid value '${value}'. Expected ${expectedFormat}.`);
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

    function buildRulesPayload() {
      const allowedWeekdays = new Set([
        'monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday'
      ]);
      const includedWeekdays = validateList(
        linesToArray(document.getElementById('includedWeekdays').value),
        'IncludedWeekdays',
        value => allowedWeekdays.has(value.toLowerCase()),
        'a weekday name from Monday to Sunday');
      const startTimesToSkip = validateList(
        linesToArray(document.getElementById('startTimesToSkip').value),
        'StartTimesToSkip',
        isValidTime,
        'HH:mm (00:00-23:59)');
      const favoriteShiftUsers = validateList(
        linesToArray(document.getElementById('favoriteShiftUsers').value),
        'FavoriteShiftUsers',
        value => value.length <= 100,
        'a name up to 100 characters');
      const holidays = validateList(
        linesToArray(document.getElementById('holidays').value),
        'Holidays',
        isValidDate,
        'a real date in yyyy-MM-dd format');
      const excludedDates = validateList(
        linesToArray(document.getElementById('excludedDates').value),
        'ExcludedDates',
        isValidDate,
        'a real date in yyyy-MM-dd format');

      return {
        takeLunch: document.getElementById('takeLunch').checked,
        shiftMinHoursAhead: readInteger('shiftMinHoursAhead', 'ShiftMinHoursAhead', 1, 720),
        weekendOrHolidayMinHoursAhead: readInteger('weekendOrHolidayMinHoursAhead', 'WeekendOrHolidayMinHoursAhead', 1, 720),
        importantShiftNotificationCount: readInteger('importantShiftNotificationCount', 'ImportantShiftNotificationCount', 1, 20),
        importantShiftNotificationDelayMilliseconds: readInteger('importantShiftNotificationDelayMilliseconds', 'ImportantShiftNotificationDelayMilliseconds', 0, 600000),
        includedWeekdays,
        startTimesToSkip,
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
        await refreshAll();
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
      document.getElementById('includedWeekdays').value = arrayToLines(rules.includedWeekdays);
      document.getElementById('startTimesToSkip').value = arrayToLines(rules.startTimesToSkip);
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
        alert('Saved');
      } catch (e) {
        errorElement.innerText = e.message;
      }
    }

    async function loadLogs() {
      const result = await api('/api/logs/today?limit=300');
      document.getElementById('logs').innerText = (result.lines || []).join('\n');
    }

    async function refreshAll() {
      await Promise.all([loadStatus(), loadRules(), loadLogs()]);
      setInterval(loadStatus, 10000);
    }

    async function init() {
      try {
        await api('/api/status');
        document.getElementById('loginCard').classList.add('hidden');
        document.getElementById('app').classList.remove('hidden');
        await refreshAll();
      } catch {
        document.getElementById('loginCard').classList.remove('hidden');
        document.getElementById('app').classList.add('hidden');
      }
    }

    init();
  </script>
</body>
</html>";
        }
    }
}
