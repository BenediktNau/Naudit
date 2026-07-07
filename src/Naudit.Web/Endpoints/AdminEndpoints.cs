using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Ui;

namespace Naudit.Web.Endpoints;

/// <summary>Admin-Verwaltung: Accounts anlegen (lokal ⇒ sofort aktiv), Freigabe-Lifecycle,
/// GitHub-Links. Jeder Handler prüft Adminrechte DB-frisch (kein Policy-Caching).</summary>
public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/accounts").RequireAuthorization();

        group.MapGet("/", async (HttpContext ctx, NauditDbContext db) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();

            var accounts = await db.Accounts.Include(a => a.GitHubLinks).OrderBy(a => a.Username).ToListAsync(ctx.RequestAborted);
            var stats = await db.Projects
                .Where(p => p.AccountId != null)
                .GroupBy(p => p.AccountId!.Value)
                .Select(g => new
                {
                    AccountId = g.Key,
                    Projects = g.Count(),
                    Tokens = g.SelectMany(p => p.Reviews).Sum(r => (r.InputTokens ?? 0) + (r.OutputTokens ?? 0)),
                })
                .ToDictionaryAsync(x => x.AccountId, ctx.RequestAborted);

            var dtos = accounts.Select(a => ToDto(a,
                stats.TryGetValue(a.Id, out var s) ? s.Projects : 0,
                stats.TryGetValue(a.Id, out var t) ? t.Tokens : 0)).ToList();
            return Results.Ok(new
            {
                pending = dtos.Where(d => d.Status == "Pending"),
                approved = dtos.Where(d => d.Status == "Active"),
            });
        });

        group.MapPost("/", async (HttpContext ctx, NauditDbContext db, AccountService accounts, CreateAccountRequest body) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
            try
            {
                var acct = await accounts.CreateLocalAsync(
                    body.Username, body.Password, body.IsAdmin ?? false, body.GitHubLogins ?? [], ctx.RequestAborted);
                return Results.Created($"/api/accounts/{acct.Id}", ToDto(acct, 0, 0));
            }
            catch (InvalidOperationException ex)
            {
                // Duplikat / Validierungsfehler — Meldung ist bewusst nutzerfreundlich formuliert.
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapPost("/{id:int}/approve", (HttpContext ctx, NauditDbContext db, AccountService svc, int id) =>
            Transition(ctx, db, svc, id, AccountStatus.Active));
        group.MapPost("/{id:int}/reject", (HttpContext ctx, NauditDbContext db, AccountService svc, int id) =>
            Transition(ctx, db, svc, id, AccountStatus.Rejected));
        group.MapPost("/{id:int}/revoke", (HttpContext ctx, NauditDbContext db, AccountService svc, int id) =>
            Transition(ctx, db, svc, id, AccountStatus.Rejected));

        group.MapPut("/{id:int}/github-links", async (HttpContext ctx, NauditDbContext db, AccountService svc, int id, SetLinksRequest body) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
            return await svc.SetGitHubLinksAsync(id, body.Logins, ctx.RequestAborted) ? Results.Ok() : Results.NotFound();
        });
    }

    private static async Task<IResult> Transition(HttpContext ctx, NauditDbContext db, AccountService svc, int id, AccountStatus status)
    {
        if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
        if (status == AccountStatus.Rejected)
        {
            // Kein aktiver Admin darf entzogen werden (auch nicht der Aufrufer selbst) — sonst Total-Lockout.
            // Ein pending Admin-Signup darf hingegen abgelehnt werden.
            var target = await db.Accounts.FindAsync([id], ctx.RequestAborted);
            if (target is null) return Results.NotFound();
            if (target is { IsAdmin: true, Status: AccountStatus.Active })
                return Results.Conflict(new { error = "Active admin accounts cannot be revoked." });
        }
        return await svc.SetStatusAsync(id, status, ctx.RequestAborted) ? Results.Ok() : Results.NotFound();
    }

    private static AccountDto ToDto(AccountEntity a, int projects, long tokens) => new(
        a.Id, a.Username, a.Provider.ToString(), a.Status.ToString(), a.IsAdmin, a.CreatedAt,
        a.GitHubLinks.Select(l => l.Login).ToList(), projects, tokens);
}

public sealed record CreateAccountRequest(string Username, string Password, bool? IsAdmin, List<string>? GitHubLogins);
public sealed record SetLinksRequest(List<string> Logins);
public sealed record AccountDto(int Id, string Username, string Provider, string Status, bool IsAdmin,
    DateTime CreatedAt, List<string> GitHubLogins, int ProjectCount, long TotalTokens);
