using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Ui;

namespace Naudit.Web.Endpoints;

/// <summary>BFF-Auth: lokaler Login (immer), Challenge-Redirects für GitHub/OIDC (opt-in),
/// Logout und /api/me als Status-Endpoint fürs SPA (AuthGate).</summary>
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app, UiOptions ui)
    {
        app.MapPost("/auth/login", async (LoginRequest body, AccountService accounts, HttpContext ctx) =>
        {
            var acct = await accounts.VerifyPasswordAsync(body.Username, body.Password, ctx.RequestAborted);
            if (acct is null)
                return Results.Unauthorized();
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, BuildPrincipal(acct));
            return Results.Ok(new { username = acct.Username });
        });

        app.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        app.MapGet("/api/me", async (HttpContext ctx, NauditDbContext db) =>
        {
            var providers = new { local = true, gitHub = ui.Auth.GitHub.Enabled, oidc = ui.Auth.Oidc.Enabled };
            var acct = await CurrentAccount.GetAsync(ctx, db); // beliebiger Status — pending sieht seinen Zustand
            return acct is null
                ? Results.Ok(new { isAuthenticated = false, username = (string?)null, isAdmin = false, status = (string?)null, authProviders = providers })
                : Results.Ok(new { isAuthenticated = true, username = (string?)acct.Username, isAdmin = acct.IsAdmin, status = (string?)acct.Status.ToString(), authProviders = providers });
        });
    }

    /// <summary>Eigene, schlanke Claims: Id + Username. Alles Weitere (Admin/Status) wird pro
    /// Request DB-frisch gelesen (CurrentAccount) — Approve/Revoke wirkt damit sofort.</summary>
    public static ClaimsPrincipal BuildPrincipal(AccountEntity acct)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, acct.Id.ToString()), new Claim(ClaimTypes.Name, acct.Username)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }
}

/// <summary>Request-Body des lokalen Logins.</summary>
public sealed record LoginRequest(string Username, string Password);
