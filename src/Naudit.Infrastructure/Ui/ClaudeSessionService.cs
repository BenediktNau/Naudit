using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Ui;

/// <summary>Verwaltet den pro Account hinterlegten Claude-Code-OAuth-Token (Autor-Sessions).
/// Token liegt DP-verschlüsselt in der DB; Entschlüsselung nur unmittelbar vor dem CLI-Lauf.</summary>
public sealed class ClaudeSessionService(
    NauditDbContext db,
    IDataProtectionProvider dataProtection,
    SessionContainerManager? sandbox = null,
    ILogger<ClaudeSessionService>? logger = null)
{
    public const string ProtectorPurpose = "Naudit.AiSessions";

    /// <exception cref="GitAuthorLoginConflictException">Login gehört bereits einem anderen Account.</exception>
    public async Task SetTokenAsync(int accountId, string token, string? gitAuthorLogin, CancellationToken ct = default)
    {
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, ct);

        // Login-Zuordnung: explizit gesetzt gewinnt; sonst bei GitHub-Accounts der Username
        // (dort ist Username = GitHub-Login). Immer lowercased (case-insensitiver Match).
        var login = (!string.IsNullOrWhiteSpace(gitAuthorLogin) ? gitAuthorLogin
            : account.GitAuthorLogin ?? (account.Provider == AccountProvider.GitHub ? account.Username : null))
            ?.Trim().ToLowerInvariant();
        // Konfliktprüfung VOR jeder Änderung am (getrackten) Entity — ein 409 hinterlässt nichts.
        if (login is not null)
            await EnsureLoginUnclaimedAsync(accountId, login, ct);

        account.ClaudeSessionToken = dataProtection.CreateProtector(ProtectorPurpose).Protect(token);
        account.ClaudeSessionUpdatedAtUtc = DateTime.UtcNow;
        account.GitAuthorLogin = login;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Nur den Login ändern (Token unangetastet) — leer/null ⇒ No-Op.</summary>
    /// <exception cref="GitAuthorLoginConflictException">Login gehört bereits einem anderen Account.</exception>
    public async Task SetLoginAsync(int accountId, string? gitAuthorLogin, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gitAuthorLogin)) return;
        var login = gitAuthorLogin.Trim().ToLowerInvariant();
        await EnsureLoginUnclaimedAsync(accountId, login, ct);
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, ct);
        account.GitAuthorLogin = login;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>True, wenn ein ANDERER Account den (normalisierten) Login bereits beansprucht.
    /// Sonst könnte jeder Nutzer fremde Logins "claimen" und deren MRs auf sein Abo umleiten.</summary>
    public Task<bool> IsLoginClaimedByOtherAsync(int accountId, string gitAuthorLogin, CancellationToken ct = default)
    {
        var login = gitAuthorLogin.Trim().ToLowerInvariant();
        return db.Accounts.AnyAsync(a => a.Id != accountId && a.GitAuthorLogin == login, ct);
    }

    private async Task EnsureLoginUnclaimedAsync(int accountId, string login, CancellationToken ct)
    {
        if (await IsLoginClaimedByOtherAsync(accountId, login, ct))
            throw new GitAuthorLoginConflictException(login);
    }

    public async Task RemoveTokenAsync(int accountId, CancellationToken ct = default)
    {
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, ct);
        account.ClaudeSessionToken = null;
        account.ClaudeSessionUpdatedAtUtc = null;
        await db.SaveChangesAsync(ct);
        await RemoveSandboxAsync(accountId, ct);
    }

    /// <summary>Aktiver Account mit Token zum Autor-Login, oder null. Mehrdeutige Altdaten
    /// (mehrere Accounts mit demselben Login, vor der Konfliktprüfung entstanden) ⇒ null + Warnung:
    /// lieber der globale Client als still das "falsche" Abo (Routing-Hijack).</summary>
    public async Task<AccountEntity?> FindByAuthorLoginAsync(string authorLogin, CancellationToken ct = default)
    {
        var login = authorLogin.Trim().ToLowerInvariant();
        var matches = await db.Accounts
            .Where(a => a.GitAuthorLogin == login && a.Status == AccountStatus.Active && a.ClaudeSessionToken != null)
            .OrderBy(a => a.Id)
            .Take(2)
            .ToListAsync(ct);
        if (matches.Count > 1)
        {
            logger?.LogWarning(
                "Autor-Login {Login} ist mehreren Accounts zugeordnet — mehrdeutig, Review läuft über den globalen Client. " +
                "Doppelte Zuordnung im Profil bereinigen.", login);
            return null;
        }
        return matches.SingleOrDefault();
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
        if (!share)
            await RemoveSandboxAsync(accountId, ct);
    }

    /// <summary>Nicht entschlüsselbar (Keyring weg, fremder Ciphertext, kaputtes Base64url) ⇒ null
    /// statt Crash — gleiche Semantik wie DbSettingsLoader bei Settings-Secrets.</summary>
    public string? DecryptToken(AccountEntity account)
    {
        if (account.ClaudeSessionToken is null) return null;
        try { return dataProtection.CreateProtector(ProtectorPurpose).Unprotect(account.ClaudeSessionToken); }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or FormatException) { return null; }
    }

    // Sandbox-Lifecycle: ohne Token/Pool-Opt-in soll der Account-Container samt Volume (enthält
    // CLI-Credentials!) verschwinden. Best-effort — ein Docker-Fehler kippt nie die DB-Operation.
    // Im Author-Modus kostet ein Pool-Austritt so schlimmstenfalls einen Kaltstart beim nächsten Review.
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
                "Session-Sandbox: Container-Abbau für Konto {AccountId} fehlgeschlagen (best-effort).", accountId);
        }
    }
}

/// <summary>Der Autor-Login ist bereits einem anderen Account zugeordnet (⇒ HTTP 409).</summary>
public sealed class GitAuthorLoginConflictException(string login)
    : InvalidOperationException($"Autor-Login '{login}' ist bereits einem anderen Account zugeordnet.")
{
    public string Login { get; } = login;
}
