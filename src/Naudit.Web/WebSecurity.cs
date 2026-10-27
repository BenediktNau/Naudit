using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Naudit.Web;

/// <summary>Web-Härtung der WebUI/BFF-Fläche: Login-Rate-Limit, Same-Origin-Schutz (CSRF) für
/// zustandsändernde Requests und Security-Header. Maschinen-Endpoints (Webhooks, POST /review)
/// liegen außerhalb von /api und /auth und bleiben unberührt.</summary>
public static class WebSecurity
{
    /// <summary>Rate-Limit-Policy für POST /auth/login und POST /api/setup/admin.</summary>
    public const string LoginRateLimitPolicy = "naudit-login";

    /// <summary>Fixed-Window-Limiter pro Client-IP (nach ForwardedHeaders ⇒ die echte Client-IP
    /// hinter dem Proxy). Konfigurierbar über Naudit:Ui:LoginRateLimit:PermitLimit (Default 10)
    /// und :WindowSeconds (Default 60); Überschreitung ⇒ 429.</summary>
    public static IServiceCollection AddNauditLoginRateLimiter(this IServiceCollection services, IConfiguration config)
    {
        var section = config.GetSection("Naudit:Ui:LoginRateLimit");
        var permitLimit = Math.Max(1, section.GetValue("PermitLimit", 10));
        var window = TimeSpan.FromSeconds(Math.Max(1, section.GetValue("WindowSeconds", 60)));

        return services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(LoginRateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueLimit = 0, // nicht anstauen — sofort 429
                }));
        });
    }

    /// <summary>Security-Header auf jeder Antwort: kein MIME-Sniffing, kein Framing (Clickjacking),
    /// Referrer nur als Origin cross-site. Bewusst NICHT no-referrer: damit sendeten Browser bei
    /// same-origin-POSTs "Origin: null", und der Origin-Fallback des CSRF-Schutzes bräche.</summary>
    public static IApplicationBuilder UseNauditSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((ctx, next) =>
        {
            var headers = ctx.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Content-Security-Policy"] = "frame-ancestors 'none'";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            return next(ctx);
        });

    /// <summary>CSRF-Schutz für zustandsändernde Requests auf /api und /auth. SameSite=Lax allein
    /// schützt nicht gegen Geschwister-Subdomains (same-site) und nicht bei body-losen POSTs.
    /// Regel: Sec-Fetch-Site vorhanden ⇒ nur same-origin/none; sonst Origin (falls vorhanden) gegen
    /// das eigene Scheme+Host. Ohne beide Header (curl, CI, Skripte) ⇒ durchlassen — kein Browser,
    /// also kein Ambient-Cookie-Angriff. Ausgenommen: /auth/callback/* (OAuth/OIDC-Rückkehr kommt
    /// legitim cross-site, z. B. OIDC form_post; dort schützen state/nonce).</summary>
    public static IApplicationBuilder UseNauditSameOriginGuard(this IApplicationBuilder app, bool isDevelopment) =>
        app.Use((ctx, next) =>
        {
            if (!IsUnsafeMethod(ctx.Request.Method) || !IsProtectedPath(ctx.Request.Path)
                || IsSameOriginRequest(ctx.Request, isDevelopment))
                return next(ctx);

            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return ctx.Response.WriteAsJsonAsync(new { error = "cross-origin request rejected" });
        });

    private static bool IsUnsafeMethod(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

    private static bool IsProtectedPath(PathString path) =>
        (path.StartsWithSegments("/api") || path.StartsWithSegments("/auth"))
        && !path.StartsWithSegments("/auth/callback");

    internal static bool IsSameOriginRequest(HttpRequest request, bool isDevelopment)
    {
        var site = request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0)
            // "none" = vom Nutzer selbst ausgelöst (Adressleiste/Lesezeichen), nie von fremder Seite.
            return site.Equals("same-origin", StringComparison.OrdinalIgnoreCase)
                || site.Equals("none", StringComparison.OrdinalIgnoreCase);

        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
            return true; // kein Browser-Request

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            return false; // auch "Origin: null" (opaker Ursprung, z. B. Sandbox-iframe)

        // Dev: Vite-Proxy (localhost:5173 ⇒ localhost:5290) ändert den Port — Loopback-Origins erlauben.
        if (isDevelopment && originUri.IsLoopback)
            return true;

        // Request.Scheme ist nach UseForwardedHeaders das öffentliche Scheme (X-Forwarded-Proto).
        var ownPort = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        return originUri.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase)
            && originUri.Host.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase)
            && originUri.Port == ownPort;
    }
}
