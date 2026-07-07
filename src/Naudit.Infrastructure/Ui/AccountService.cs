using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Ui;

/// <summary>Account-Verwaltung: lokale Nutzer (Admin legt an ⇒ sofort aktiv), externe
/// Materialisierung (Self-Service ⇒ pending), Status-Übergänge, GitHub-Links, Seed-Admin.</summary>
public sealed class AccountService(NauditDbContext db, UiOptions options)
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
        return Hasher.VerifyHashedPassword(acct, acct.PasswordHash, password) == PasswordVerificationResult.Failed
            ? null : acct;
    }

    public async Task<AccountEntity> MaterializeExternalAsync(
        AccountProvider provider, string externalId, string username, string? gitHubLogin, CancellationToken ct = default)
    {
        var acct = await db.Accounts.Include(a => a.GitHubLinks)
            .SingleOrDefaultAsync(a => a.Provider == provider && a.ExternalId == externalId, ct);
        if (acct is not null)
            return acct;

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
        await db.SaveChangesAsync(ct);
        return acct;
    }

    public async Task<bool> SetStatusAsync(int id, AccountStatus status, CancellationToken ct = default)
    {
        var acct = await db.Accounts.FindAsync([id], ct);
        if (acct is null) return false;
        acct.Status = status;
        await db.SaveChangesAsync(ct);
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
}
