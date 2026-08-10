using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace GymBeam.AdminManager.Security;

internal static class AuthenticationEndpoints
{
    internal const string SessionCookieName = "gb_manager_session";
    internal const string CsrfCookieName = "gb_manager_csrf";
    internal const string SessionItemKey = "AdminManager.AuthenticatedSession";
    private const string LoginCsrfContext = "login";

    public static void UseApiSecurityHeaders(this WebApplication application)
    {
        application.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers.XContentTypeOptions = "nosniff";
            }

            await next(context);
        });
    }

    public static void UseAuthentication(this WebApplication application)
    {
        application.Use(async (context, next) =>
        {
            if (!RequiresAuthentication(context.Request.Path))
            {
                await next(context);
                return;
            }

            SessionManager sessions = context.RequestServices.GetRequiredService<SessionManager>();
            context.Request.Cookies.TryGetValue(SessionCookieName, out string? token);
            SessionTokenPayload? session = await sessions.ValidateAsync(token, context.RequestAborted);
            if (session is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(
                    new { error = "Authentication required" },
                    context.RequestAborted);
                return;
            }

            context.Items[SessionItemKey] = session;
            await next(context);
        });
    }

    public static void UseCsrfProtection(this WebApplication application)
    {
        application.Use(async (context, next) =>
        {
            if (!RequiresCsrfProtection(context.Request))
            {
                await next(context);
                return;
            }

            string csrfContext = context.Request.Path.Equals("/api/auth/login", StringComparison.Ordinal)
                ? LoginCsrfContext
                : GetSession(context).SessionId;
            string headerToken = context.Request.Headers["X-CSRF-Token"].ToString();
            context.Request.Cookies.TryGetValue(CsrfCookieName, out string? cookieToken);
            CsrfTokenService csrf = context.RequestServices.GetRequiredService<CsrfTokenService>();
            if (!TokensMatch(headerToken, cookieToken) || !csrf.Validate(headerToken, csrfContext))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(
                    new { error = "CSRF validation failed" },
                    context.RequestAborted);
                return;
            }

            await next(context);
        });
    }

    public static void MapAuthenticationEndpoints(this WebApplication application)
    {
        application.MapPost("/api/auth/login", LoginAsync);
        application.MapPost("/api/auth/logout", LogoutAsync);
        application.MapGet("/api/auth/csrf", IssueCsrfAsync);
        application.MapGet("/api/auth/me", (HttpContext context) =>
        {
            SessionTokenPayload session = GetSession(context);
            return Results.Json(new
            {
                username = session.Username,
                expiresAtUtc = session.ExpiresAtUtc
            });
        });
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context,
        AdminManagerOptions options,
        SessionManager sessions,
        CsrfTokenService csrf,
        LoginRateLimiter rateLimiter,
        AuditLogger audit)
    {
        context.Response.Headers.CacheControl = "no-store";
        string remoteAddress = GetRemoteAddress(context);
        if (!rateLimiter.TryAcquire(remoteAddress, out TimeSpan retryAfter))
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            await audit.WriteAsync(
                "auth.login",
                "rate_limited",
                actor: null,
                target: null,
                remoteAddress,
                context.RequestAborted);
            return Results.Json(
                new { error = "Too many login attempts" },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        LoginRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync<LoginRequest>(context.RequestAborted);
        }
        catch (JsonException)
        {
            await audit.WriteAsync(
                "auth.login",
                "invalid_request",
                actor: null,
                target: null,
                remoteAddress,
                context.RequestAborted);
            return Results.BadRequest(new { error = "Invalid request" });
        }

        if (request is null
            || request.Username is null
            || request.Password is null
            || request.Username.Length > 256
            || request.Password.Length > 1024)
        {
            await audit.WriteAsync(
                "auth.login",
                "invalid_request",
                actor: null,
                target: null,
                remoteAddress,
                context.RequestAborted);
            return Results.BadRequest(new { error = "Invalid request" });
        }

        bool passwordMatches = PasswordHasher.Verify(request.Password, options.Security.PasswordHash);
        bool usernameMatches = string.Equals(
            request.Username,
            options.Security.Username,
            StringComparison.Ordinal);
        if (!(passwordMatches & usernameMatches))
        {
            await audit.WriteAsync(
                "auth.login",
                "failure",
                actor: usernameMatches ? options.Security.Username : null,
                target: null,
                remoteAddress,
                context.RequestAborted);
            return Results.Json(
                new { error = "Invalid credentials" },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        (string token, SessionTokenPayload payload) = await sessions.CreateAsync(
            options.Security.Username,
            context.RequestAborted);
        string csrfToken = csrf.Create(payload.SessionId);
        try
        {
            await audit.WriteAsync(
                "auth.login",
                "success",
                payload.Username,
                target: null,
                remoteAddress,
                context.RequestAborted);
        }
        catch
        {
            await sessions.RevokeAsync(payload, CancellationToken.None);
            throw;
        }

        context.Response.Cookies.Append(
            SessionCookieName,
            token,
            CreateSessionCookieOptions(payload.ExpiresAtUtc, options.Security.SessionLifetime));
        SetCsrfCookie(context, csrfToken);
        return Results.Json(new
        {
            username = payload.Username,
            expiresAtUtc = payload.ExpiresAtUtc,
            csrfToken
        });
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        SessionManager sessions,
        AuditLogger audit)
    {
        SessionTokenPayload session = GetSession(context);
        await sessions.RevokeAsync(session, context.RequestAborted);
        context.Response.Cookies.Append(
            SessionCookieName,
            string.Empty,
            CreateExpiredCookieOptions());
        context.Response.Cookies.Append(
            CsrfCookieName,
            string.Empty,
            CreateExpiredCookieOptions(httpOnly: true));
        await audit.WriteAsync(
            "auth.logout",
            "success",
            session.Username,
            target: null,
            GetRemoteAddress(context),
            context.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> IssueCsrfAsync(
        HttpContext context,
        SessionManager sessions,
        CsrfTokenService csrf)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Request.Cookies.TryGetValue(SessionCookieName, out string? sessionToken);
        SessionTokenPayload? session = await sessions.ValidateAsync(sessionToken, context.RequestAborted);
        string csrfToken = csrf.Create(session?.SessionId ?? LoginCsrfContext);
        SetCsrfCookie(context, csrfToken);
        return Results.Json(new { csrfToken });
    }

    private static bool RequiresAuthentication(PathString path)
    {
        return path.Equals("/", StringComparison.Ordinal)
            || (path.StartsWithSegments("/api")
                && !path.Equals("/api/auth/login", StringComparison.Ordinal)
                && !path.Equals("/api/auth/csrf", StringComparison.Ordinal));
    }

    private static bool RequiresCsrfProtection(HttpRequest request)
    {
        return request.Path.StartsWithSegments("/api")
            && !HttpMethods.IsGet(request.Method)
            && !HttpMethods.IsHead(request.Method)
            && !HttpMethods.IsOptions(request.Method);
    }

    private static bool TokensMatch(string headerToken, string? cookieToken)
    {
        if (string.IsNullOrEmpty(headerToken) || string.IsNullOrEmpty(cookieToken))
        {
            return false;
        }

        byte[] headerBytes = Encoding.UTF8.GetBytes(headerToken);
        byte[] cookieBytes = Encoding.UTF8.GetBytes(cookieToken);
        return headerBytes.Length == cookieBytes.Length
            && CryptographicOperations.FixedTimeEquals(headerBytes, cookieBytes);
    }

    private static void SetCsrfCookie(HttpContext context, string token)
    {
        context.Response.Cookies.Append(
            CsrfCookieName,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                IsEssential = true,
                MaxAge = CsrfTokenService.TokenLifetime
            });
    }

    private static string GetRemoteAddress(HttpContext context)
    {
        IPAddress? proxyAddress = context.Connection.RemoteIpAddress;
        string forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (IsPrivateOrLoopback(proxyAddress)
            && forwarded.Length is > 0 and <= 64
            && !forwarded.Contains(',')
            && IPAddress.TryParse(forwarded, out IPAddress? clientAddress))
        {
            return clientAddress.ToString();
        }

        return proxyAddress?.ToString() ?? "unknown";
    }

    private static bool IsPrivateOrLoopback(IPAddress? address)
    {
        if (address is null || IPAddress.IsLoopback(address))
        {
            return address is not null;
        }

        byte[] bytes = address.MapToIPv4().GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254);
    }

    private static SessionTokenPayload GetSession(HttpContext context)
    {
        return (SessionTokenPayload)context.Items[SessionItemKey]!;
    }

    private static CookieOptions CreateSessionCookieOptions(
        DateTimeOffset expiresAtUtc,
        TimeSpan lifetime)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            Expires = expiresAtUtc,
            MaxAge = lifetime
        };
    }

    private static CookieOptions CreateExpiredCookieOptions(bool httpOnly = true)
    {
        return new CookieOptions
        {
            HttpOnly = httpOnly,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            Expires = DateTimeOffset.UnixEpoch,
            MaxAge = TimeSpan.Zero
        };
    }

    private sealed record LoginRequest(string? Username, string? Password);
}
