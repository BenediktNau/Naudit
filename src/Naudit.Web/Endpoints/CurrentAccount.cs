using System.Security.Claims;
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
}
