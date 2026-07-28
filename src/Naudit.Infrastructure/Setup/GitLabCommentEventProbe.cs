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
        [property: JsonPropertyName("note_events")] bool NoteEvents);

    public async Task<CommentEventStatus> CheckAsync(CancellationToken ct = default)
    {
        // Ohne bekannte öffentliche URL fehlt der Vergleichsmaßstab: welcher der Hooks Naudits
        // ist, wäre geraten. Dann lieber keine Aussage — und keine API-Last.
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
            return CommentEventStatus.Unknown;
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
            return CommentEventStatus.Unknown;
        }

        var details = new List<string>();
        var anyChecked = false;
        foreach (var projectId in projects)
        {
            var noteEvents = await ProbeProjectAsync(projectId, webhookUrl, ct);
            if (noteEvents is null)
                continue;   // nicht ermittelbar oder kein Projekt-Hook — keine Aussage
            anyChecked = true;
            if (!noteEvents.Value)
                details.Add(
                    $"Antwort-Kommandos sind für GitLab-Projekt {projectId} wirkungslos — der " +
                    "Naudit-Webhook hat den Trigger \"Comments\" (note_events) nicht. @naudit fp / " +
                    "@naudit ok werden nie zugestellt. Beheben: Projekt → Settings → Webhooks → den " +
                    "Naudit-Hook bearbeiten → \"Comments\" anhaken → Save.");
        }

        if (details.Count > 0)
            return new CommentEventStatus(CommentEventState.Missing, details);
        return anyChecked ? CommentEventStatus.Ok : CommentEventStatus.Unknown;
    }

    /// <summary>true/false = note_events des Naudit-Hooks; null = keine Aussage möglich.</summary>
    private async Task<bool?> ProbeProjectAsync(string projectId, string webhookUrl, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"api/v4/projects/{Uri.EscapeDataString(projectId)}/hooks");
            req.Headers.Add("PRIVATE-TOKEN", await tokens.ResolveTokenAsync(projectId, ct));
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogDebug("Kommentar-Event-Prüfung: /hooks für Projekt {Project} lieferte {Status}.",
                    projectId, (int)res.StatusCode);
                return null;
            }

            var hooks = JsonSerializer.Deserialize<List<HookDto>>(await res.Content.ReadAsStringAsync(ct));
            var hook = hooks?.FirstOrDefault(h =>
                string.Equals(h.Url?.TrimEnd('/'), webhookUrl, StringComparison.OrdinalIgnoreCase));
            // Kein passender Projekt-Hook ⇒ null, NICHT false: ein Gruppen-Hook wirkt auf das
            // Projekt, taucht in dieser Liste aber nie auf. Eine Warnung wäre dort dauerhaft falsch.
            return hook?.NoteEvents;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Kommentar-Event-Prüfung für GitLab-Projekt {Project} fehlgeschlagen.", projectId);
            return null;
        }
    }
}
