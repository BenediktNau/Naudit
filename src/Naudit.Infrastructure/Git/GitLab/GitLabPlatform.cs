using System.Net.Http.Json;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git.GitLab;

/// <summary>IGitPlatform-Implementierung für GitLab. BaseAddress + PRIVATE-TOKEN kommen vom typed HttpClient.</summary>
public sealed class GitLabPlatform(HttpClient http) : IGitPlatform
{
    public async Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var url = $"api/v4/projects/{request.ProjectId}/merge_requests/{request.MergeRequestIid}/changes";
        var response = await http.GetFromJsonAsync<GitLabChangesResponse>(url, ct);
        if (response?.Changes is null)
            return [];

        return response.Changes
            .Select(c => new CodeChange(c.NewPath, c.Diff))
            .ToList();
    }

    public async Task PostSummaryAsync(ReviewRequest request, string markdown, CancellationToken ct = default)
    {
        var url = $"api/v4/projects/{request.ProjectId}/merge_requests/{request.MergeRequestIid}/notes";
        var response = await http.PostAsJsonAsync(url, new { body = markdown }, ct);
        response.EnsureSuccessStatusCode();
    }
}
