using Microsoft.EntityFrameworkCore;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Ui;

/// <summary>DB-gestützte Zugangsschranke: der Owner-Anteil der ProjectId ("owner/repo" ⇒ "owner",
/// GitLab-Id ⇒ ganzer Wert) muss als GitHub-Link eines AKTIVEN Accounts hinterlegt sein.</summary>
public sealed class EfAccessGate(NauditDbContext db) : IAccessGate
{
    public async Task<bool> IsAllowedAsync(string projectId, CancellationToken ct = default)
    {
        var owner = OwnerOf(projectId);
        if (owner.Length == 0) return false;
        return await db.GitHubLinks.AnyAsync(
            l => l.Login == owner && l.Account.Status == AccountStatus.Active, ct);
    }

    /// <summary>Links werden lowercased gespeichert ⇒ Vergleich per Gleichheit reicht.</summary>
    internal static string OwnerOf(string projectId) =>
        projectId.Split('/')[0].Trim().ToLowerInvariant();
}
