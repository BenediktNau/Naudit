using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git.GitLab;

/// <summary>IGitPlatform-Implementierung für GitLab. BaseAddress + PRIVATE-TOKEN kommen vom typed HttpClient.</summary>
public sealed class GitLabPlatform(HttpClient http, IOptions<GitLabOptions> options) : IGitPlatform
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
            // GitLab verlangt old_path UND new_path im text-Position-Payload – auch für
            // hinzugefügte Zeilen. old_line wird nur bei vorhandener alter Position gesetzt.
            var position = new Dictionary<string, object?>
            {
                ["position_type"] = "text",
                ["base_sha"] = refs.BaseSha,
                ["head_sha"] = refs.HeadSha,
                ["start_sha"] = refs.StartSha,
                ["old_path"] = c.FilePath,
                ["new_path"] = c.FilePath,
                ["new_line"] = c.NewLine,
            };
            // Kontextzeile: zusätzlich die alte Zeilennummer angeben.
            if (c.OldLine is int oldLine)
                position["old_line"] = oldLine;

            var payload = new { body = c.Body, position };
            (await http.PostAsJsonAsync($"{basePath}/discussions", payload, ct)).EnsureSuccessStatusCode();
        }
    }

    public async Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var project = await http.GetFromJsonAsync<GitLabProject>($"api/v4/projects/{request.ProjectId}", ct)
            ?? throw new InvalidOperationException("GitLab lieferte keine Projekt-Infos.");
        if (string.IsNullOrEmpty(project.HttpUrlToRepo))
            throw new InvalidOperationException("GitLab lieferte keine http_url_to_repo.");

        // Token in die Klon-URL einbetten (oauth2:<token>@host).
        var cloneUrl = project.HttpUrlToRepo.Replace("://", $"://oauth2:{options.Value.Token}@");
        return new RepoCheckoutInfo(cloneUrl, $"refs/merge-requests/{request.MergeRequestIid}/head");
    }
}
