using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Web.Endpoints;

/// <summary>Löst den eingeloggten Account DB-frisch auf — Status/Adminrechte sind nie stale.</summary>
public static class CurrentAccount
{
    public static async Task<AccountEntity?> GetAsync(HttpContext ctx, NauditDbContext db)
    {
        var idClaim = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (idClaim is null || !int.TryParse(idClaim, out var id))
            return null;
        return await db.Accounts.FindAsync([id], ctx.RequestAborted);
    }

    public static async Task<AccountEntity?> GetActiveAsync(HttpContext ctx, NauditDbContext db)
    {
        var acct = await GetAsync(ctx, db);
        return acct?.Status == AccountStatus.Active ? acct : null;
    }

    public static async Task<AccountEntity?> GetAdminAsync(HttpContext ctx, NauditDbContext db)
    {
        var acct = await GetActiveAsync(ctx, db);
        return acct?.IsAdmin == true ? acct : null;
    }

    /// <summary>Admin: alle Projekte. Sonst: Projekte, deren Owner-Anteil in den eigenen Links liegt.
    /// (Aus DataEndpoints hierher gezogen — jetzt von Data- UND Memory-API genutzt.)</summary>
    public static IQueryable<ProjectEntity> VisibleProjects(NauditDbContext db, AccountEntity acct)
    {
        if (acct.IsAdmin) return db.Projects;
        var logins = db.GitHubLinks.Where(l => l.AccountId == acct.Id).Select(l => l.Login);
        // Owner = Teil vor '/'; GitLab-Ids matchen als Ganzes (Links sind lowercased gespeichert).
        return db.Projects.Where(p =>
            logins.Any(l => p.PlatformProjectId.ToLower() == l || EF.Functions.Like(p.PlatformProjectId.ToLower(), l + "/%")));
    }

    public static Task<bool> CanSeeProjectAsync(NauditDbContext db, AccountEntity acct, int projectId, CancellationToken ct)
        => VisibleProjects(db, acct).AnyAsync(p => p.Id == projectId, ct);
}
