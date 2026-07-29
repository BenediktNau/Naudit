using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;

namespace Naudit.Infrastructure.Setup;

/// <summary>Prüft je Projekt den Naudit-Webhook auf note_events. Projektauswahl sind die
/// ProjectEntity-Zeilen (Projekte mit mindestens einem Review), jüngste zuerst und gedeckelt —
/// eine frische Installation hat keine und wird still übersprungen.</summary>
public sealed class GitLabCommentEventProbe(
    HttpClient http, IGitTokenProvider tokens, NauditDbContext db,
    string publicBaseUrl, ILogger<GitLabCommentEventProbe> logger) : ICommentEventProbe
{
    public const int MaxProjects = 20;

    private sealed record HookDto(
        [property: JsonPropertyName("url")] string? Url,
        // bool? statt bool: ein passender Hook, dessen JSON note_events schlicht nicht enthält,
        // deserialisiert sonst zu false — und ein Feld, das nie gesetzt wurde, ist etwas anderes
        // als eines, das nachweislich auf false steht. Nur Letzteres darf warnen.
        [property: JsonPropertyName("note_events")] bool? NoteEvents);

    public async Task<CommentEventStatus> CheckAsync(CancellationToken ct = default)
    {
        // Ohne bekannte öffentliche URL fehlt der Vergleichsmaßstab: welcher der Hooks Naudits
        // ist, wäre geraten. Dann lieber keine Aussage — und keine API-Last.
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
            return new CommentEventStatus(CommentEventState.Unknown, [],
                "nicht ermittelbar (kein Naudit:PublicBaseUrl konfiguriert).");
        var webhookUrl = $"{publicBaseUrl.TrimEnd('/')}/webhook/gitlab";

        List<string> projects;
        try
        {
            projects = await db.Projects
                .OrderByDescending(p => p.LastReviewedAt)
                .Take(MaxProjects)
                .Select(p => p.PlatformProjectId)
                .ToListAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Kommentar-Event-Prüfung: Projektliste nicht lesbar.");
            return new CommentEventStatus(CommentEventState.Unknown, [],
                "nicht ermittelbar (Projektliste nicht lesbar).");
        }

        if (projects.Count == 0)
            return new CommentEventStatus(CommentEventState.Unknown, [],
                "keine Projekte vorhanden (noch kein Review durchgeführt).");

        var details = new List<string>();
        var checkedCount = 0;
        var unknownCount = 0;
        foreach (var projectId in projects)
        {
            var noteEvents = await ProbeProjectAsync(projectId, webhookUrl, ct);
            if (noteEvents is null)
            {
                unknownCount++;
                continue;   // nicht ermittelbar oder kein Projekt-Hook — keine Aussage
            }
            checkedCount++;
            if (!noteEvents.Value)
                details.Add(
                    $"Antwort-Kommandos sind für GitLab-Projekt {projectId} wirkungslos — der " +
                    "Naudit-Webhook hat den Trigger \"Comments\" (note_events) nicht. @naudit fp / " +
                    "@naudit ok werden nie zugestellt. Beheben: Projekt → Settings → Webhooks → den " +
                    "Naudit-Hook bearbeiten → \"Comments\" anhaken → Save.");
        }

        var summary = checkedCount > 0
            ? $"{checkedCount} Projekte geprüft" + (unknownCount > 0
                ? $", {unknownCount} nicht ermittelbar (403 oder kein Projekt-Hook)."
                : ".")
            : $"nicht ermittelbar ({unknownCount} von {projects.Count} Projekten ohne Ergebnis, " +
              "403 oder kein Projekt-Hook).";

        if (details.Count > 0)
            return new CommentEventStatus(CommentEventState.Missing, details, summary);
        return checkedCount > 0
            ? new CommentEventStatus(CommentEventState.Ok, [], summary)
            : new CommentEventStatus(CommentEventState.Unknown, [], summary);
    }

    /// <summary>true/false = note_events eines passenden Naudit-Hooks; null = keine Aussage
    /// möglich (kein passender Hook, HTTP-Fehler, oder alle passenden Hooks ohne das Feld).</summary>
    private async Task<bool?> ProbeProjectAsync(string projectId, string webhookUrl, CancellationToken ct)
    {
        try
        {
            // per_page=100: die API paginiert standardmäßig bei 20 — der Naudit-Hook darf nicht
            // auf einer zweiten Seite verschwinden. 100 ist das API-Maximum; ein Projekt mit mehr
            // Hooks bliebe unerkannt. Bewusst nicht paginiert: die Fehlerrichtung ist still
            // (kein Treffer ⇒ null ⇒ "nicht ermittelbar"), nie eine falsche Warnung.
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"api/v4/projects/{Uri.EscapeDataString(projectId)}/hooks?per_page=100");
            req.Headers.Add("PRIVATE-TOKEN", await tokens.ResolveTokenAsync(projectId, ct));
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogDebug("Kommentar-Event-Prüfung: /hooks für Projekt {Project} lieferte {Status}.",
                    projectId, (int)res.StatusCode);
                return null;
            }

            var hooks = JsonSerializer.Deserialize<List<HookDto>>(await res.Content.ReadAsStringAsync(ct));
            var matching = hooks?.Where(h =>
                string.Equals(h.Url?.TrimEnd('/'), webhookUrl, StringComparison.OrdinalIgnoreCase)).ToList()
                ?? [];
            // Kein passender Projekt-Hook ⇒ null, NICHT false: ein Gruppen-Hook wirkt auf das
            // Projekt, taucht in dieser Liste aber nie auf. Eine Warnung wäre dort dauerhaft falsch.
            if (matching.Count == 0)
                return null;
            // "Irgendein" passender Hook trägt note_events, nicht der erste: bei zwei Hooks auf
            // dieselbe URL (z. B. ein veralteter neben einem korrekt nachgepflegten) darf ein
            // veralteter, führender Hook ohne das Feld einen korrekten zweiten nicht überdecken.
            if (matching.Any(h => h.NoteEvents == true))
                return true;
            if (matching.Any(h => h.NoteEvents == false))
                return false;
            // Alle passenden Hooks haben das Feld schlicht nicht im JSON — keine Aussage möglich.
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Kommentar-Event-Prüfung für GitLab-Projekt {Project} fehlgeschlagen.", projectId);
            return null;
        }
    }
}
