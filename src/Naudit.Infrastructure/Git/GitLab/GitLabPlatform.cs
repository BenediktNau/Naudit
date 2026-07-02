using System.Net.Http.Json;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git.GitLab;

/// <summary>IGitPlatform-Implementierung für GitLab. BaseAddress kommt vom typed HttpClient; der
/// PRIVATE-TOKEN wird pro Request aus dem projekt-aufgelösten <see cref="IGitTokenProvider"/> gesetzt
/// (Per-Projekt-Token).</summary>
public sealed class GitLabPlatform(HttpClient http, IGitTokenProvider tokens) : IGitPlatform
{
    public async Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var url = $"api/v4/projects/{request.ProjectId}/merge_requests/{request.MergeRequestIid}/changes";
        using var response = await SendAsync(HttpMethod.Get, url, request.ProjectId, null, ct);
        response.EnsureSuccessStatusCode();
        var changes = await response.Content.ReadFromJsonAsync<GitLabChangesResponse>(ct);
        if (changes?.Changes is null)
            return [];

        return changes.Changes
            .Select(c => new CodeChange(c.NewPath, c.Diff))
            .ToList();
    }

    public async Task PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, CancellationToken ct = default)
    {
        var basePath = $"api/v4/projects/{request.ProjectId}/merge_requests/{request.MergeRequestIid}";

        // 1) Summary als normale Note.
        (await SendAsync(HttpMethod.Post, $"{basePath}/notes", request.ProjectId, new { body = summaryMarkdown }, ct)).EnsureSuccessStatusCode();

        if (comments.Count == 0)
            return;

        // 2) diff_refs (base/head/start SHA) für die Discussion-Position holen.
        using var detailResponse = await SendAsync(HttpMethod.Get, basePath, request.ProjectId, null, ct);
        detailResponse.EnsureSuccessStatusCode();
        var detail = await detailResponse.Content.ReadFromJsonAsync<GitLabMergeRequestDetail>(ct);
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
            (await SendAsync(HttpMethod.Post, $"{basePath}/discussions", request.ProjectId, payload, ct)).EnsureSuccessStatusCode();
        }
    }

    public async Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v4/projects/{request.ProjectId}", request.ProjectId, null, ct);
        response.EnsureSuccessStatusCode();
        var project = await response.Content.ReadFromJsonAsync<GitLabProject>(ct)
            ?? throw new InvalidOperationException("GitLab lieferte keine Projekt-Infos.");
        if (string.IsNullOrEmpty(project.HttpUrlToRepo))
            throw new InvalidOperationException("GitLab lieferte keine http_url_to_repo.");

        // Token in die Klon-URL einbetten (oauth2:<token>@host) — projekt-aufgelöst.
        var token = await tokens.ResolveTokenAsync(request.ProjectId, ct);
        var cloneUrl = project.HttpUrlToRepo.Replace("://", $"://oauth2:{token}@");
        return new RepoCheckoutInfo(cloneUrl, $"refs/merge-requests/{request.MergeRequestIid}/head");
    }

    // Auth pro Request: GitLab nutzt den PRIVATE-TOKEN-Header mit dem projekt-aufgelösten Token.
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string projectId, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Add("PRIVATE-TOKEN", await tokens.ResolveTokenAsync(projectId, ct));
        if (body is not null)
            req.Content = JsonContent.Create(body);
        return await http.SendAsync(req, ct);
    }
}
