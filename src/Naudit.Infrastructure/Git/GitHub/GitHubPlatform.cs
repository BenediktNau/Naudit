using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git.GitHub;

/// <summary>IGitPlatform-Implementierung für GitHub. BaseAddress + statische Header kommen vom typed
/// HttpClient; der Auth-Token wird pro Request aus dem projekt-aufgelösten <see cref="IGitTokenProvider"/>
/// gesetzt (Per-Projekt-Token).</summary>
public sealed class GitHubPlatform(
    HttpClient http, IGitTokenProvider tokens, IOptions<GitHubOptions> options,
    ILogger<GitHubPlatform>? logger = null) : IGitPlatform
{
    private readonly ILogger<GitHubPlatform> _logger = logger ?? NullLogger<GitHubPlatform>.Instance;

    public async Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default)
    {
        // ProjectId enthält "owner/repo". Eine Seite (per_page=100) reicht für normale PRs (bewusste POC-Grenze).
        var url = $"repos/{request.ProjectId}/pulls/{request.MergeRequestIid}/files?per_page=100";
        using var response = await SendAsync(HttpMethod.Get, url, request.ProjectId, null, ct);
        response.EnsureSuccessStatusCode();
        var files = await response.Content.ReadFromJsonAsync<List<GitHubFile>>(ct);
        if (files is null)
            return [];

        // Dateien ohne patch (binär/zu groß) überspringen.
        return files
            .Where(f => !string.IsNullOrEmpty(f.Patch))
            .Select(f => new CodeChange(f.Filename, f.Patch!))
            .ToList();
    }

    public async Task<IReadOnlyList<PostedComment>> PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, ReviewVerdict verdict, CancellationToken ct = default)
    {
        // Ein Review-Call trägt Summary (body) UND alle Inline-Kommentare.
        // Echtes Verdikt nur opt-in (PostVerdict) — Default bleibt COMMENT (kein Review-Status;
        // Naudit gatet nicht automatisch über GitHubs eigenen Review-Status).
        var url = $"repos/{request.ProjectId}/pulls/{request.MergeRequestIid}/reviews";
        var @event = !options.Value.PostVerdict ? "COMMENT"
            : verdict == ReviewVerdict.RequestChanges ? "REQUEST_CHANGES" : "APPROVE";
        using var response = await PostReviewOnceAsync(url, request, summaryMarkdown, comments, @event, ct);

        // GitHub lehnt APPROVE/REQUEST_CHANGES u. a. vom PR-Autor mit 422 ab. Der Review-Inhalt
        // (Summary + Inline-Kommentare) darf dabei nicht verloren gehen: einmal als COMMENT nachposten.
        if (@event != "COMMENT" && response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            _logger.LogWarning(
                "GitHub hat das Review-Verdikt {Event} für {Project}#{Number} abgelehnt (422) — Review wird ohne Verdikt als COMMENT gepostet (typisch: Token-Identität ist der PR-Autor; GitHub-App oder Service-Account verwenden).",
                @event, request.ProjectId, request.MergeRequestIid);
            using var fallback = await PostReviewOnceAsync(url, request, summaryMarkdown, comments, "COMMENT", ct);
            fallback.EnsureSuccessStatusCode();
            return await CaptureCommentIdsAsync(request, fallback, comments, ct);
        }
        response.EnsureSuccessStatusCode();
        return await CaptureCommentIdsAsync(request, response, comments, ct);
    }

    // Nach erfolgreichem Post: Review-Id lesen, dessen Kommentare holen und je Inline-Kommentar
    // per (Pfad, Zeile) die Review-Comment-Id zuordnen. Best-effort: jeder Fehler ⇒ null-Ids,
    // der Review ist bereits gepostet und darf nicht mehr kippen.
    private async Task<IReadOnlyList<PostedComment>> CaptureCommentIdsAsync(
        ReviewRequest request, HttpResponseMessage reviewResponse,
        IReadOnlyList<InlineComment> comments, CancellationToken ct)
    {
        if (comments.Count == 0)
            return [];   // keine Inline-Kommentare ⇒ nichts zu matchen, kein GET nötig.

        var fallback = comments.Select(_ => new PostedComment(null, null)).ToList();
        try
        {
            var review = await reviewResponse.Content.ReadFromJsonAsync<GitHubReviewResponse>(ct);
            if (review is null)
                return fallback;
            using var resp = await SendAsync(HttpMethod.Get,
                $"repos/{request.ProjectId}/pulls/{request.MergeRequestIid}/reviews/{review.Id}/comments?per_page=100",
                request.ProjectId, null, ct);
            resp.EnsureSuccessStatusCode();
            var posted = await resp.Content.ReadFromJsonAsync<List<GitHubReviewComment>>(ct) ?? [];

            // Zuordnung bewusst OHNE `line`: dieser Endpunkt liefert die Legacy-Repräsentation, in
            // der `line` immer null ist (nur `position`). Ein Vergleich (Pfad, Zeile) trifft deshalb
            // nie. Stattdessen je Pfad der Reihe nach — GitHub vergibt die Ids in Sendereihenfolge
            // (an echten Daten geprüft, siehe Commit-Message zu diesem Block).
            //
            // Die Stückzahl je Pfad muss exakt passen. Weicht sie ab, ist die Ausrichtung innerhalb
            // dieses Pfades nicht mehr belegbar — dann lieber KEINE Id als eine falsche: eine
            // vertauschte Id würde ein späteres `@naudit fp` am falschen Finding vermerken und den
            // Fehleintrag dauerhaft ins Projekt-Gedächtnis schreiben. Der Fall wird geloggt, damit
            // er auffällt, statt still zu passieren.
            var postedByPath = posted
                .GroupBy(p => p.Path ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).ToList());

            var result = new PostedComment[comments.Count];
            foreach (var group in comments.Select((c, i) => (Comment: c, Index: i)).GroupBy(x => x.Comment.FilePath))
            {
                var mine = group.ToList();
                var ids = postedByPath.TryGetValue(group.Key, out var l) ? l : [];

                if (ids.Count != mine.Count)
                {
                    _logger.LogWarning(
                        "Comment-Id-Erfassung für {Project}#{Pr}, Datei {File}: {Posted} gepostete Kommentare, "
                        + "{Expected} erwartet — Zuordnung nicht belegbar, Ids bleiben leer.",
                        request.ProjectId, request.MergeRequestIid, group.Key, ids.Count, mine.Count);
                    foreach (var (_, index) in mine)
                        result[index] = new PostedComment(null, null);
                    continue;
                }

                for (var k = 0; k < mine.Count; k++)
                    result[mine[k].Index] = new PostedComment(ids[k].Id.ToString(), null);
            }
            return result;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return fallback;   // Erfassung best-effort — der Review steht bereits.
        }
    }

    private Task<HttpResponseMessage> PostReviewOnceAsync(
        string url, ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments,
        string @event, CancellationToken ct)
    {
        var payload = new
        {
            body = summaryMarkdown,
            @event,
            comments = comments.Select(c => new
            {
                path = c.FilePath,
                line = c.NewLine,
                side = "RIGHT",
                body = c.Body,
            }).ToArray(),
        };
        return SendAsync(HttpMethod.Post, url, request.ProjectId, payload, ct);
    }

    public async Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"repos/{request.ProjectId}", request.ProjectId, null, ct);
        response.EnsureSuccessStatusCode();
        var repo = await response.Content.ReadFromJsonAsync<GitHubRepository>(ct)
            ?? throw new InvalidOperationException("GitHub lieferte keine Repo-Infos.");
        if (string.IsNullOrEmpty(repo.CloneUrl))
            throw new InvalidOperationException("GitHub lieferte keine clone_url.");

        // Token in die Klon-URL einbetten (x-access-token:<token>@host) — projekt-aufgelöst.
        var token = await tokens.ResolveTokenAsync(request.ProjectId, ct);
        var cloneUrl = repo.CloneUrl.Replace("://", $"://x-access-token:{token}@");
        return new RepoCheckoutInfo(cloneUrl, $"refs/pull/{request.MergeRequestIid}/head");
    }

    // Auth pro Request aus dem projekt-aufgelösten Token — nicht als Default-Header (Per-Projekt-fähig, thread-safe).
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string projectId, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.ResolveTokenAsync(projectId, ct));
        if (body is not null)
            req.Content = JsonContent.Create(body);
        return await http.SendAsync(req, ct);
    }
}
