using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Review;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Guidelines;

/// <summary>Destilliert aus der Repo-Doku (CLAUDE.md, AGENTS.md, README, docs/…) ein kompaktes
/// Architektur-Profil und hält es hash-gecacht in der DB: unveränderte Doku ⇒ NULL LLM-Calls.
/// Menschliche Kuration gewinnt (ManuallyEdited blockt Auto-Refresh; SourcesChangedAt = Stale-Signal).
/// Fail-open: jeder Fehler ⇒ gespeichertes Profil bzw. null, geloggt — kippt nie das Review.</summary>
public sealed class DistillingReviewGuidelines(
    NauditDbContext db, IChatClient chatClient, IPromptRedactor redactor,
    ReviewOptions options, ILogger<DistillingReviewGuidelines> logger) : IReviewGuidelines
{
    private const string DistillSystemPrompt =
        "You extract binding project rules for a code reviewer. From the repository documentation below, " +
        "extract the 10-20 binding architecture and convention rules of this project as a terse Markdown bullet list " +
        "(one rule per bullet, imperative, no headings, no commentary). " +
        "Only include rules actually stated in the documentation - never invent rules. " +
        "Prefer rules a code review can enforce: layering and dependency direction, endpoint or API contracts, " +
        "error-handling policies, security requirements, naming and testing conventions. " +
        "If the documentation contains no such rules, respond with an empty string.";

    public async Task<string?> GetAsync(string projectId, string? workspaceDir, CancellationToken ct = default)
    {
        try
        {
            var project = await db.Projects.SingleOrDefaultAsync(p => p.PlatformProjectId == projectId, ct);
            var stored = project is null
                ? null
                : await db.ProjectGuidelines.SingleOrDefaultAsync(g => g.ProjectId == project.Id, ct);

            if (workspaceDir is null)
                return Emit(stored?.Markdown);   // kein Checkout ⇒ gespeichertes Profil (oder nichts)

            var sources = await CollectSourcesAsync(workspaceDir, ct);
            if (sources.Count == 0)
                return Emit(stored?.Markdown);

            var hash = ComputeHash(sources);
            if (stored is not null && stored.SourceHash == hash)
                return Emit(stored.Markdown);    // unveränderte Doku ⇒ kein LLM-Call

            if (stored is not null && stored.ManuallyEdited)
            {
                // Menschliche Kuration gewinnt: nie überschreiben, nur das Stale-Signal setzen.
                // Ausnahme Erst-Kuration: der PUT-Endpoint hat keinen Checkout und legt die Zeile mit
                // SourceHash "" an — das ist KEINE Baseline. Aktuellen Quellstand übernehmen statt
                // sofort ein falsches "Doku geändert" zu melden (das zum Verwerfen der frischen
                // Kuration via Re-Distill verleiten würde).
                if (stored.SourceHash.Length == 0)
                {
                    stored.SourceHash = hash;
                    await db.SaveChangesAsync(ct);
                }
                else if (stored.SourcesChangedAt is null)
                {
                    stored.SourcesChangedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                return Emit(stored.Markdown);
            }

            string profile;
            try
            {
                profile = await DistillAsync(sources, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Guidelines-Destillat für {Project} fehlgeschlagen — nutze gespeichertes Profil.", projectId);
                return Emit(stored?.Markdown);
            }

            if (project is null)
            {
                // Allererstes Review: die Projekt-Zeile (inkl. Ownership) legt erst der Audit-Sink
                // NACH dem Review an — Profil liefern, aber nicht speichern (FK-Ziel fehlt bewusst).
                return Emit(profile);
            }

            if (stored is null)
            {
                stored = new ProjectGuidelinesEntity
                {
                    ProjectId = project.Id, Markdown = profile, SourceHash = hash,
                    DistilledAt = DateTime.UtcNow, UpdatedBy = "naudit",
                };
                db.ProjectGuidelines.Add(stored);
            }
            else
            {
                stored.Markdown = profile;
                stored.SourceHash = hash;
                stored.DistilledAt = DateTime.UtcNow;
                stored.SourcesChangedAt = null;
                stored.UpdatedBy = "naudit";
            }
            try
            {
                await db.SaveChangesAsync(ct);   // auch ein leeres Destillat speichern: der Hash verhindert Re-Destillieren
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Speichern fehlgeschlagen (z. B. Concurrent-First-Store-Race auf dem Unique-Index, DB kurz weg):
                // das frische, bereits per LLM bezahlte Profil trotzdem für DIESES Review nutzen, nur nicht persistiert.
                // Die gescheiterte Entität detachen — der Kontext ist im Review-Scope geteilt (Audit-Sink!),
                // getrackt bliebe der kaputte Insert und ließe jedes spätere SaveChangesAsync erneut scheitern.
                db.Entry(stored).State = EntityState.Detached;
                logger.LogWarning(ex, "Guidelines-Speichern für {Project} fehlgeschlagen — Profil dieses Reviews genutzt, nicht persistiert.", projectId);
            }
            return Emit(profile);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Guidelines-Abruf für {Project} fehlgeschlagen — Review läuft ohne Profil weiter.", projectId);
            return null;
        }
    }

    // Leeres/Whitespace-Profil ⇒ null (PromptBuilder lässt die Sektion dann weg).
    private static string? Emit(string? markdown)
        => string.IsNullOrWhiteSpace(markdown) ? null : markdown;

    private async Task<string> DistillAsync(IReadOnlyList<(string Path, string Content)> sources, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var (path, content) in sources)
        {
            sb.AppendLine($"## {path}");
            sb.AppendLine(await redactor.RedactAsync(content, ct));   // Quellen wie jeden Prompt-Bestandteil maskieren
            sb.AppendLine();
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, DistillSystemPrompt),
            new(ChatRole.User, sb.ToString()),
        };
        var response = await chatClient.GetResponseAsync(messages, new ChatOptions(), ct);
        var profile = response.Text.Trim();
        return CapProfile(profile, options.Guidelines.MaxProfileChars);
    }

    private static string CapProfile(string s, int cap)
    {
        // Ungültiger Deckel (≤ 0, z. B. Tippfehler auf der Settings-Seite) deckelt nicht, statt mit
        // IndexOutOfRange zu crashen — der fail-open-Wrapper würde das schlucken, aber den Hash-Cache
        // still lahmlegen (jedes Review zahlte den LLM-Call erneut).
        if (cap <= 0 || s.Length <= cap)
            return s;
        var end = cap;
        // Kein Surrogat-Paar (z. B. Emoji) zerschneiden — sonst bleibt ein ungültiges lone surrogate stehen.
        if (char.IsHighSurrogate(s[end - 1]))
            end--;
        return s[..end];
    }

    // Deterministische Sammlung (stabile Reihenfolge ⇒ stabiler Hash): exakte Namen in Sources-Reihenfolge,
    // "dir/**/*.md"-Muster rekursiv sortiert. Dateien, die das Restbudget sprengen, werden GANZ übersprungen.
    private async Task<List<(string Path, string Content)>> CollectSourcesAsync(string root, CancellationToken ct)
    {
        var result = new List<(string, string)>();
        var budget = options.Guidelines.MaxSourceChars;

        foreach (var pattern in options.Guidelines.Sources)
        {
            foreach (var file in ResolvePattern(root, pattern))
            {
                // Byte-Länge VOR dem Einlesen prüfen: der Checkout enthält Fremd-Content (PR eines
                // externen Contributors) — eine absichtlich riesige .md-Datei darf den Prozess nicht
                // erst voll in den Speicher zwingen. UTF-8 → UTF-16: 1 char belegt höchstens 3 Bytes
                // (4-Byte-Sequenzen ergeben 2 chars), d. h. > budget*3 Bytes ⇒ sicher > budget chars.
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > (long)budget * 3)
                    continue;
                var content = await File.ReadAllTextAsync(file, ct);
                if (content.Length > budget)
                    continue;
                budget -= content.Length;
                result.Add((Path.GetRelativePath(root, file).Replace('\\', '/'), content));
            }
        }
        return result;
    }

    private static IEnumerable<string> ResolvePattern(string root, string pattern)
    {
        const string recursiveMd = "/**/*.md";
        if (pattern.EndsWith(recursiveMd, StringComparison.Ordinal))
        {
            var dir = Path.Combine(root, pattern[..^recursiveMd.Length]);
            if (!Directory.Exists(dir))
                return [];
            return Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal);
        }
        return [Path.Combine(root, pattern)];
    }

    private static string ComputeHash(IReadOnlyList<(string Path, string Content)> sources)
    {
        var sb = new StringBuilder();
        foreach (var (path, content) in sources)
        {
            sb.Append(path).Append('\n').Append(content).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
