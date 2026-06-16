using System.Net.Http.Json;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git.GitHub;

/// <summary>IGitPlatform-Implementierung für GitHub. BaseAddress + Auth-Header kommen vom typed HttpClient.</summary>
public sealed class GitHubPlatform(HttpClient http) : IGitPlatform
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

    public async Task PostSummaryAsync(ReviewRequest request, string markdown, CancellationToken ct = default)
    {
        // PR-Kommentar = Issue-Kommentar (gleiche Nummer).
        var url = $"repos/{request.ProjectId}/issues/{request.MergeRequestIid}/comments";
        var response = await http.PostAsJsonAsync(url, new { body = markdown }, ct);
        response.EnsureSuccessStatusCode();
    }
}
