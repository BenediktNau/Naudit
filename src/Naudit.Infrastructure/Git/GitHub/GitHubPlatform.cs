using System.Net.Http.Headers;
using System.Net.Http.Json;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git.GitHub;

/// <summary>IGitPlatform-Implementierung für GitHub. BaseAddress + statische Header kommen vom typed
/// HttpClient; der Auth-Token wird pro Request aus dem projekt-aufgelösten <see cref="IGitTokenProvider"/>
/// gesetzt (Per-Projekt-Token).</summary>
public sealed class GitHubPlatform(HttpClient http, IGitTokenProvider tokens) : IGitPlatform
{
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

    public async Task PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, CancellationToken ct = default)
    {
        // Ein Review-Call trägt Summary (body) UND alle Inline-Kommentare. event=COMMENT:
        // Naudit gatet nicht über GitHubs eigenen Review-Status (Verdict läuft über ReviewResult).
        var url = $"repos/{request.ProjectId}/pulls/{request.MergeRequestIid}/reviews";
        var payload = new
        {
            body = summaryMarkdown,
            @event = "COMMENT",
            comments = comments.Select(c => new
            {
                path = c.FilePath,
                line = c.NewLine,
                side = "RIGHT",
                body = c.Body,
            }).ToArray(),
        };
        using var response = await SendAsync(HttpMethod.Post, url, request.ProjectId, payload, ct);
        response.EnsureSuccessStatusCode();
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
