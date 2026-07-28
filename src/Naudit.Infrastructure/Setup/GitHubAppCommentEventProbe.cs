using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Git.GitHub;

namespace Naudit.Infrastructure.Setup;

/// <summary>GET /app liefert die Ereignisliste der eigenen App. Nur bei Auth=App registriert —
/// im PAT-Modus gibt es keine App, deren Liste man abfragen könnte.</summary>
public sealed class GitHubAppCommentEventProbe(
    HttpClient http, GitHubAppJwt jwt, ILogger<GitHubAppCommentEventProbe> logger) : ICommentEventProbe
{
    public const string RequiredEvent = "pull_request_review_comment";

    public async Task<CommentEventStatus> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "app");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt.Create());
            using var res = await http.SendAsync(req, ct);
            if (res.StatusCode != HttpStatusCode.OK)
            {
                logger.LogDebug("Kommentar-Event-Prüfung: GET /app lieferte {Status}.", (int)res.StatusCode);
                return CommentEventStatus.Unknown;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("events", out var events)
                || events.ValueKind != JsonValueKind.Array)
                return CommentEventStatus.Unknown;

            foreach (var e in events.EnumerateArray())
                if (string.Equals(e.GetString(), RequiredEvent, StringComparison.Ordinal))
                    return CommentEventStatus.Ok;

            // Ohne Slug keinen halbfertigen Link bauen — dann lieber im Klartext hinweisen.
            var slug = doc.RootElement.TryGetProperty("slug", out var s) ? s.GetString() : null;
            var where = string.IsNullOrEmpty(slug)
                ? "den Einstellungen der GitHub-App"
                : $"https://github.com/settings/apps/{slug}/permissions";

            return new CommentEventStatus(CommentEventState.Missing, [
                $"Antwort-Kommandos sind wirkungslos — die GitHub-App ist nicht auf '{RequiredEvent}' " +
                "abonniert. @naudit fp / @naudit ok werden nie zugestellt. Beheben: " +
                $"{where} → \"Subscribe to events\" → \"Pull request review comment\" anhaken → Save. " +
                "Wirkt sofort für bestehende Installationen; es ändert sich keine Permission, also " +
                "sind weder Neuinstallation noch Bestätigung durch die Nutzer nötig."]);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Kommentar-Event-Prüfung der GitHub-App fehlgeschlagen.");
            return CommentEventStatus.Unknown;
        }
    }
}
