using Microsoft.EntityFrameworkCore;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Ui;

/// <summary>Der Audit-Log ist der Zähler: gezählt werden die vorhandenen ReviewEntity-Zeilen
/// pro (Projekt, PR). No-Op-Läufe (keine Änderungen ⇒ kein Audit) zählen damit automatisch nie.</summary>
public sealed class EfReviewRoundtripCounter(NauditDbContext db) : IReviewRoundtripCounter
{
    public Task<int> CountAsync(string projectId, int mergeRequestIid, CancellationToken ct = default)
        => db.Reviews.CountAsync(
            r => r.Project.PlatformProjectId == projectId && r.PrNumber == mergeRequestIid, ct);
}
