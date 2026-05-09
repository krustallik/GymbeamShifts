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
        public List<string> IncludedWeekdays { get; set; } = new List<string>();
        public List<string> StartTimesToSkip { get; set; } = new List<string>();
        public List<string> Holidays { get; set; } = new List<string>();
        public List<string> ExcludedDates { get; set; } = new List<string>();
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
        private readonly HttpListener _listener = new HttpListener();
        private readonly AppConfig _config;
        private readonly ShiftRulesStore _shiftRulesStore;
        private readonly Func<BotStatusSnapshot> _statusProvider;
        private readonly object _loginAttemptsLock = new object();
        private readonly Dictionary<string, List<DateTime>> _loginAttemptsByIp = new Dictionary<string, List<DateTime>>();
        private Thread? _serverThread;

        public AdminWebServer(AppConfig config, ShiftRulesStore shiftRulesStore, Func<BotStatusSnapshot> statusProvider)
        {
            _config = config;
            _shiftRulesStore = shiftRulesStore;
            _statusProvider = statusProvider;
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
                ExpireSessionCookie(context.Response);
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
                WriteJson(context.Response, 200, _shiftRulesStore.GetSnapshot());
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
            var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(body);
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
            SetSessionCookie(context.Response, token);
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
            return request.RemoteEndPoint?.Address.ToString() ?? "unknown";
        }

        private void HandleShiftRulesUpdate(HttpListenerContext context)
        {
            try
            {
                var body = ReadRequestBody(context.Request);
                var update = JsonSerializer.Deserialize<ShiftRulesUpdateRequest>(body);
                if (update == null)
                {
                    WriteJson(context.Response, 400, new { error = "Invalid payload" });
                    return;
                }

                var updatedRules = _shiftRulesStore.Update(update);
                _config.ShiftRules = updatedRules;
                ConfigurationLoader.SaveShiftRules(AppConstants.ConfigFileName, updatedRules);

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

        private static void SetSessionCookie(HttpListenerResponse response, string token)
        {
            response.Cookies.Add(new Cookie(SessionCookieName, token)
            {
                HttpOnly = true,
                Path = "/",
                Expires = DateTime.Now.AddDays(SessionLifetimeDays)
            });
        }

        private static void ExpireSessionCookie(HttpListenerResponse response)
        {
            response.Cookies.Add(new Cookie(SessionCookieName, string.Empty)
            {
                HttpOnly = true,
                Path = "/",
                Expires = DateTime.Now.AddDays(-1)
            });
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
    button { background: #2563eb; cursor: pointer; margin-top: 10px; }
    button:hover { background:#1d4ed8; }
    .grid { display:grid; gap:16px; grid-template-columns: 1fr 1fr; }
    .hidden { display:none; }
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
        <div class='grid'>
          <div>
            <label>IncludedWeekdays (one per line)</label>
            <textarea id='includedWeekdays' rows='6'></textarea>
            <label>StartTimesToSkip (one per line)</label>
            <textarea id='startTimesToSkip' rows='6'></textarea>
          </div>
          <div>
            <label>Holidays yyyy-MM-dd (one per line)</label>
            <textarea id='holidays' rows='6'></textarea>
            <label>ExcludedDates yyyy-MM-dd (one per line)</label>
            <textarea id='excludedDates' rows='6'></textarea>
          </div>
        </div>
        <button onclick='saveRules()'>Save ShiftRules</button>
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
      document.getElementById('includedWeekdays').value = arrayToLines(rules.includedWeekdays);
      document.getElementById('startTimesToSkip').value = arrayToLines(rules.startTimesToSkip);
      document.getElementById('holidays').value = arrayToLines(rules.holidays);
      document.getElementById('excludedDates').value = arrayToLines(rules.excludedDates);
    }

    async function saveRules() {
      const payload = {
        includedWeekdays: linesToArray(document.getElementById('includedWeekdays').value),
        startTimesToSkip: linesToArray(document.getElementById('startTimesToSkip').value),
        holidays: linesToArray(document.getElementById('holidays').value),
        excludedDates: linesToArray(document.getElementById('excludedDates').value)
      };
      await api('/api/shift-rules', { method: 'PUT', body: JSON.stringify(payload) });
      await loadRules();
      alert('Saved');
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
