using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;

namespace Naudit.Web.Endpoints;

/// <summary>Installations-Status der Naudit-GitHub-App für den eingeloggten User (Onboarding-Banner).
/// Nur gemappt, wenn Plattform=GitHub UND Auth=App — sonst existiert die Route nicht (404), und das
/// SPA zeigt kein Banner. Auch für Pending-Accounts erreichbar: das Banner soll gerade in der
/// Wartezeit erscheinen.</summary>
public static class GitHubAppEndpoints
{
    public static void MapGitHubAppEndpoints(this WebApplication app, GitOptions git, GitHubOptions gitHub)
    {
        if (git.Platform != GitPlatformKind.GitHub || gitHub.Auth != GitHubAuthKind.App)
            return;

        app.MapGet("/api/me/github-app",
            async (HttpContext ctx, NauditDbContext db, IGitHubAppInstallationChecker checker) =>
            {
                var acct = await CurrentAccount.GetAsync(ctx, db);
                if (acct is null) return Results.Unauthorized();

                var logins = await db.GitHubLinks
                    .Where(l => l.AccountId == acct.Id)
                    .Select(l => l.Login)
                    .ToListAsync(ctx.RequestAborted);

                var status = await checker.GetStatusAsync(logins, ctx.RequestAborted);
                return Results.Ok(new
                {
                    installUrl = status.InstallUrl,
                    accounts = status.Accounts.Select(a => new { login = a.Login, installed = a.Installed }),
                });
            }).RequireAuthorization();
    }
}
