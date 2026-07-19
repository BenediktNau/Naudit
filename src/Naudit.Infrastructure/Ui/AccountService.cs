using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Ui;

/// <summary>Account-Verwaltung: lokale Nutzer (Admin legt an ⇒ sofort aktiv), externe
/// Materialisierung (Self-Service ⇒ pending), Status-Übergänge, GitHub-Links, Seed-Admin.</summary>
public sealed class AccountService(
    NauditDbContext db,
    UiOptions options,
    SessionContainerManager? sandbox = null,
    ILogger<AccountService>? logger = null)
{
    private static readonly PasswordHasher<AccountEntity> Hasher = new();

    public async Task<AccountEntity> CreateLocalAsync(
        string username, string password, bool isAdmin, IReadOnlyList<string> gitHubLogins, CancellationToken ct = default)
    {
        username = username.Trim();
        if (username.Length == 0)
            throw new InvalidOperationException("Username darf nicht leer sein.");
        if (password.Length < 8)
            throw new InvalidOperationException("Passwort muss mindestens 8 Zeichen haben.");
        if (await db.Accounts.AnyAsync(a => a.Username == username, ct))
            throw new InvalidOperationException($"Username '{username}' existiert bereits.");

        var acct = new AccountEntity
        {
            Username = username,
            Provider = AccountProvider.Local,
            Status = AccountStatus.Active, // Anlegen durch den Admin IST die Freigabe
            IsAdmin = isAdmin,
            CreatedAt = DateTime.UtcNow,
        };
        acct.PasswordHash = Hasher.HashPassword(acct, password);
        foreach (var login in Normalize(gitHubLogins))
            acct.GitHubLinks.Add(new GitHubLinkEntity { Login = login });

        db.Accounts.Add(acct);
        await db.SaveChangesAsync(ct);
        return acct;
    }

    public async Task<AccountEntity?> VerifyPasswordAsync(string username, string password, CancellationToken ct = default)
    {
        var acct = await db.Accounts.SingleOrDefaultAsync(
            a => a.Username == username && a.Provider == AccountProvider.Local, ct);
        if (acct?.PasswordHash is null || acct.Status != AccountStatus.Active)
            return null;
        var result = Hasher.VerifyHashedPassword(acct, acct.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
            return null;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            // Hash mit veraltetem Format verifiziert — auf das aktuelle (stärkere) Format heben.
            acct.PasswordHash = Hasher.HashPassword(acct, password);
            await db.SaveChangesAsync(ct);
        }
        return acct;
    }

    public async Task<AccountEntity> MaterializeExternalAsync(
        AccountProvider provider, string externalId, string username, string? gitHubLogin, CancellationToken ct = default)
    {
        var acct = await db.Accounts.Include(a => a.GitHubLinks)
            .SingleOrDefaultAsync(a => a.Provider == provider && a.ExternalId == externalId, ct);
        if (acct is not null)
        {
            // Ein zuvor abgelehnter/entzogener Account, der sich erneut anmeldet, landet wieder in
            // Pending — sonst könnte der Admin ihn nie wieder freigeben (Rejected ist im Dashboard
            // unsichtbar). Active/Pending bleiben unangetastet (ein Freigegebener wird NICHT zurückgesetzt).
            if (acct.Status == AccountStatus.Rejected)
            {
                acct.Status = AccountStatus.Pending;
                await db.SaveChangesAsync(ct);
            }
            return acct;
        }

        // Username-Kollision mit anderem Account: eindeutig machen statt scheitern.
        var name = username;
        for (var i = 2; await db.Accounts.AnyAsync(a => a.Username == name, ct); i++)
            name = $"{username}-{i}";

        acct = new AccountEntity
        {
            Username = name,
            Provider = provider,
            ExternalId = externalId,
            Status = AccountStatus.Pending, // Self-Service wartet auf Admin-Freigabe
            IsAdmin = options.Admins.Contains(username, StringComparer.OrdinalIgnoreCase),
            CreatedAt = DateTime.UtcNow,
        };
        if (!string.IsNullOrWhiteSpace(gitHubLogin))
            acct.GitHubLinks.Add(new GitHubLinkEntity { Login = gitHubLogin.Trim().ToLowerInvariant() });

        db.Accounts.Add(acct);
        try
        {
            await db.SaveChangesAsync(ct);
            return acct;
        }
        catch (DbUpdateException)
        {
            // Paralleler OAuth-Callback hat denselben (Provider, ExternalId) zuerst angelegt
            // (Unique-Index greift): eigenen Insert verwerfen und den existierenden Account zurückgeben,
            // statt einen zweiten anzulegen.
            foreach (var link in acct.GitHubLinks) db.Entry(link).State = EntityState.Detached;
            db.Entry(acct).State = EntityState.Detached;
            return await db.Accounts.Include(a => a.GitHubLinks)
                .SingleAsync(a => a.Provider == provider && a.ExternalId == externalId, ct);
        }
    }

    public async Task<bool> SetStatusAsync(int id, AccountStatus status, CancellationToken ct = default)
    {
        var acct = await db.Accounts.FindAsync([id], ct);
        if (acct is null) return false;
        acct.Status = status;
        await db.SaveChangesAsync(ct);
        // Jeder Statuswechsel WEG von Active (Suspendieren/Deaktivieren/Ablehnen) räumt die
        // Sandbox ab — ein suspendierter Account darf sein Credential-Volume nicht behalten.
        // Rückkehr zu Active fasst die Sandbox bewusst nicht an (kein Auto-Warmstart bei
        // Reaktivierung, der nächste Review startet einfach kalt neu).
        if (status != AccountStatus.Active)
            await RemoveSandboxAsync(id, ct);
        return true;
    }

    public async Task<bool> SetGitHubLinksAsync(int id, IReadOnlyList<string> logins, CancellationToken ct = default)
    {
        var acct = await db.Accounts.Include(a => a.GitHubLinks).SingleOrDefaultAsync(a => a.Id == id, ct);
        if (acct is null) return false;
        acct.GitHubLinks.Clear();
        foreach (var login in Normalize(logins))
            acct.GitHubLinks.Add(new GitHubLinkEntity { Login = login });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Seed-Admin beim Start: nur wenn die Tabelle leer ist und beide Werte gesetzt sind.</summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await db.Accounts.AnyAsync(ct)) return;
        if (string.IsNullOrWhiteSpace(options.Admin.Username) || string.IsNullOrWhiteSpace(options.Admin.InitialPassword)) return;
        await CreateLocalAsync(options.Admin.Username, options.Admin.InitialPassword, isAdmin: true, [], ct);
    }

    private static IEnumerable<string> Normalize(IReadOnlyList<string> logins) =>
        logins.Select(l => l.Trim().ToLowerInvariant()).Where(l => l.Length > 0).Distinct();

    // Sandbox-Lifecycle (analog ClaudeSessionService.RemoveSandboxAsync): ein suspendierter
    // Account darf sein Credential-Volume nicht behalten — best-effort, ein Docker-Fehler
    // kippt nie die Status-Änderung.
    private async Task RemoveSandboxAsync(int accountId, CancellationToken ct)
    {
        if (sandbox is null)
            return;
        try
        {
            await sandbox.RemoveAsync(accountId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex,
                "Session-Sandbox: Container-Abbau für Konto {AccountId} nach Status-Änderung fehlgeschlagen (best-effort).", accountId);
        }
    }
}
