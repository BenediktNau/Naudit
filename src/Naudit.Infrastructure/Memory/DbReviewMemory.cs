using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Memory;

/// <summary>Deterministischer Default-Selector: alle Konventionen (File ist Anzeige, nie Filter)
/// + FPs mit exaktem Datei-Match im Diff + datei-lose FPs; Deckel MaxEntries — Konventionen
/// zuerst, dann FPs, je neueste zuerst. Fail-open: jeder Fehler ⇒ leere Liste, geloggt —
/// ein Gedächtnis-Schluckauf kippt nie das Review (Audit-Sink-Philosophie).</summary>
public sealed class DbReviewMemory(NauditDbContext db, ReviewOptions options, ILogger<DbReviewMemory> logger)
    : IReviewMemory
{
    public async Task<IReadOnlyList<MemoryEntry>> SelectAsync(
        string projectId, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        try
        {
            var project = await db.Projects.SingleOrDefaultAsync(p => p.PlatformProjectId == projectId, ct);
            if (project is null)
                return [];

            var entries = await db.MemoryEntries
                .Where(m => m.ProjectId == project.Id && m.Active)
                .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                .ToListAsync(ct);

            var files = new HashSet<string>(changes.Select(c => c.FilePath));
            var conventions = entries.Where(m => m.Kind == "Convention");
            var fps = entries.Where(m => m.Kind == "FalsePositive" && (m.File is null || files.Contains(m.File)));

            var selectedEntities = conventions.Concat(fps)
                .Take(Math.Max(0, options.Memory.MaxEntries))
                .ToList();

            var result = selectedEntities
                .Select(m => new MemoryEntry(
                    m.Kind == "Convention" ? MemoryKind.Convention : MemoryKind.FalsePositive,
                    m.File, m.Text, m.Reason))
                .ToList();

            // "Learnings applied"-Zähler: best-effort — ein Save-Fehler hier darf die bereits
            // berechnete Auswahl nicht wegwerfen, daher eigener innerer try/catch statt des
            // äußeren fail-open-catch (der würde [] zurückgeben und das ganze Memory kippen).
            var now = DateTime.UtcNow;
            foreach (var e in selectedEntities)
            {
                e.TimesApplied++;
                e.LastAppliedAtUtc = now;
            }
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "TimesApplied-Zähler-Update fehlgeschlagen — Auswahl bleibt gültig.");
            }

            return result;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Gedächtnis-Auswahl fehlgeschlagen — Review läuft ohne Memory weiter.");
            return [];
        }
    }
}
