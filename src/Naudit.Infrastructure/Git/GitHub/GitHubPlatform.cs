using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git.GitHub;

/// <summary>IGitPlatform-Implementierung für GitHub. BaseAddress + Auth-Header kommen vom typed HttpClient.</summary>
public sealed class GitHubPlatform(HttpClient http, IOptions<GitHubOptions> options) : IGitPlatform
{
    public async Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default)
    {
        // ProjectId enthält "owner/repo". Eine Seite (per_page=100) reicht für normale PRs (bewusste POC-Grenze).
        var url = $"repos/{request.ProjectId}/pulls/{request.MergeRequestIid}/files?per_page=100";
        var files = await http.GetFromJsonAsync<List<GitHubFile>>(url, ct);
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
        var response = await http.PostAsJsonAsync(url, payload, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var repo = await http.GetFromJsonAsync<GitHubRepository>($"repos/{request.ProjectId}", ct)
            ?? throw new InvalidOperationException("GitHub lieferte keine Repo-Infos.");
        if (string.IsNullOrEmpty(repo.CloneUrl))
            throw new InvalidOperationException("GitHub lieferte keine clone_url.");

        // Token in die Klon-URL einbetten (x-access-token:<token>@host).
        var cloneUrl = repo.CloneUrl.Replace("://", $"://x-access-token:{options.Value.Token}@");
        return new RepoCheckoutInfo(cloneUrl, $"refs/pull/{request.MergeRequestIid}/head");
    }
}
