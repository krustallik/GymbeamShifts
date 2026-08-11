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
                if (context.Request.Path.Equals("/", StringComparison.Ordinal))
                {
                    context.Response.Redirect("/login");
                    return;
                }

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
        application.MapGet("/login", () => Results.Content(LoginHtml, "text/html; charset=utf-8"));
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

    private const string LoginHtml = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>GymBeam Admin Manager - Login</title>
          <style>
            body{font-family:system-ui,sans-serif;margin:0;min-height:100vh;display:grid;place-items:center;background:#f6f7f9;color:#172033}
            main{width:min(22rem,calc(100% - 2rem));padding:2rem;background:white;border-radius:.75rem;box-shadow:0 .5rem 2rem #1720331a}
            h1{margin-top:0;font-size:1.5rem}label{display:block;margin-top:1rem}input{box-sizing:border-box;width:100%;margin-top:.35rem;padding:.7rem;border:1px solid #cbd5e1;border-radius:.4rem}
            button{width:100%;margin-top:1.25rem;padding:.75rem;border:0;border-radius:.4rem;background:#172033;color:white;font-weight:600;cursor:pointer}button:disabled{opacity:.6;cursor:wait}
            #error{min-height:1.25rem;margin-top:1rem;color:#b42318}
          </style>
        </head>
        <body>
          <main>
            <h1>GymBeam Admin Manager</h1>
            <form id="login-form">
              <label>Username<input name="username" autocomplete="username" required autofocus></label>
              <label>Password<input type="password" name="password" autocomplete="current-password" required></label>
              <button type="submit">Login</button>
              <div id="error" role="alert" aria-live="polite"></div>
            </form>
          </main>
          <script>
            document.getElementById('login-form').addEventListener('submit',async event=>{
              event.preventDefault();
              const form=event.currentTarget;
              const button=form.querySelector('button');
              const error=document.getElementById('error');
              button.disabled=true;error.textContent='';
              try{
                const csrfResponse=await fetch('/api/auth/csrf',{credentials:'same-origin'});
                if(!csrfResponse.ok)throw new Error('Unable to start login');
                const csrf=await csrfResponse.json();
                const fields=new FormData(form);
                const response=await fetch('/api/auth/login',{
                  method:'POST',credentials:'same-origin',
                  headers:{'Content-Type':'application/json','X-CSRF-Token':csrf.csrfToken},
                  body:JSON.stringify({username:fields.get('username'),password:fields.get('password')})
                });
                if(!response.ok){
                  const data=await response.json().catch(()=>({}));
                  throw new Error(data.error||'Login failed');
                }
                location.replace('/');
              }catch(loginError){error.textContent=loginError.message||'Login failed';}
              finally{button.disabled=false;}
            });
          </script>
        </body>
        </html>
        """;

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
