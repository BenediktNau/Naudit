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

    public async Task PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, CancellationToken ct = default)
    {
        var basePath = $"api/v4/projects/{request.ProjectId}/merge_requests/{request.MergeRequestIid}";

        // 1) Summary als normale Note.
        (await http.PostAsJsonAsync($"{basePath}/notes", new { body = summaryMarkdown }, ct)).EnsureSuccessStatusCode();

        if (comments.Count == 0)
            return;

        // 2) diff_refs (base/head/start SHA) für die Discussion-Position holen.
        var detail = await http.GetFromJsonAsync<GitLabMergeRequestDetail>(basePath, ct);
        var refs = detail?.DiffRefs
            ?? throw new InvalidOperationException("GitLab lieferte keine diff_refs für die Inline-Position.");

        // 3) Je Inline-Kommentar eine Discussion mit text-Position posten.
        foreach (var c in comments)
        {
            var position = new Dictionary<string, object?>
            {
                ["position_type"] = "text",
                ["base_sha"] = refs.BaseSha,
                ["head_sha"] = refs.HeadSha,
                ["start_sha"] = refs.StartSha,
                ["new_path"] = c.FilePath,
                ["new_line"] = c.NewLine,
            };
            // Kontextzeile: GitLab braucht zusätzlich die alte Position.
            if (c.OldLine is int oldLine)
            {
                position["old_path"] = c.FilePath;
                position["old_line"] = oldLine;
            }

            var payload = new { body = c.Body, position };
            (await http.PostAsJsonAsync($"{basePath}/discussions", payload, ct)).EnsureSuccessStatusCode();
        }
    }
}
