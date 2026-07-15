using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Ui;

/// <summary>Verwaltet den pro Account hinterlegten Claude-Code-OAuth-Token (Autor-Sessions).
/// Token liegt DP-verschlüsselt in der DB; Entschlüsselung nur unmittelbar vor dem CLI-Lauf.</summary>
public sealed class ClaudeSessionService(NauditDbContext db, IDataProtectionProvider dataProtection)
{
    public const string ProtectorPurpose = "Naudit.AiSessions";

    public async Task SetTokenAsync(int accountId, string token, string? gitAuthorLogin, CancellationToken ct = default)
    {
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, ct);
        account.ClaudeSessionToken = dataProtection.CreateProtector(ProtectorPurpose).Protect(token);
        account.ClaudeSessionUpdatedAtUtc = DateTime.UtcNow;

        // Login-Zuordnung: explizit gesetzt gewinnt; sonst bei GitHub-Accounts der Username
        // (dort ist Username = GitHub-Login). Immer lowercased (case-insensitiver Match).
        var login = !string.IsNullOrWhiteSpace(gitAuthorLogin) ? gitAuthorLogin
            : account.GitAuthorLogin ?? (account.Provider == AccountProvider.GitHub ? account.Username : null);
        account.GitAuthorLogin = login?.Trim().ToLowerInvariant();

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Nur den Login ändern (Token unangetastet) — leer/null ⇒ No-Op.</summary>
    public async Task SetLoginAsync(int accountId, string? gitAuthorLogin, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gitAuthorLogin)) return;
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, ct);
        account.GitAuthorLogin = gitAuthorLogin.Trim().ToLowerInvariant();
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveTokenAsync(int accountId, CancellationToken ct = default)
    {
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, ct);
        account.ClaudeSessionToken = null;
        account.ClaudeSessionUpdatedAtUtc = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Aktiver Account mit Token zum Autor-Login, oder null. Bei (fehlkonfigurierten)
    /// Duplikaten gewinnt deterministisch die kleinste Id.</summary>
    public Task<AccountEntity?> FindByAuthorLoginAsync(string authorLogin, CancellationToken ct = default)
    {
        var login = authorLogin.Trim().ToLowerInvariant();
        return db.Accounts
            .Where(a => a.GitAuthorLogin == login && a.Status == AccountStatus.Active && a.ClaudeSessionToken != null)
            .OrderBy(a => a.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Pool-Kandidaten fürs Round-Robin: aktive Konten mit Token UND Opt-in, Id-sortiert
    /// (deterministische Rotationsreihenfolge). Token wird erst im Router entschlüsselt.</summary>
    public Task<List<AccountEntity>> GetPoolCandidatesAsync(CancellationToken ct = default)
        => db.Accounts
            .Where(a => a.Status == AccountStatus.Active && a.ClaudeSessionToken != null && a.ShareSessionInPool)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

    /// <summary>Setzt das Pool-Opt-in (Token bleibt unangetastet).</summary>
    public async Task SetShareInPoolAsync(int accountId, bool share, CancellationToken ct = default)
    {
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, ct);
        account.ShareSessionInPool = share;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Nicht entschlüsselbar (Keyring weg, fremder Ciphertext) ⇒ null statt Crash —
    /// gleiche Semantik wie DbSettingsLoader bei Settings-Secrets.</summary>
    public string? DecryptToken(AccountEntity account)
    {
        if (account.ClaudeSessionToken is null) return null;
        try { return dataProtection.CreateProtector(ProtectorPurpose).Unprotect(account.ClaudeSessionToken); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }
}
