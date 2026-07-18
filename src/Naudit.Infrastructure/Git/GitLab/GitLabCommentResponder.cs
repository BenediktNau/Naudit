// src/Naudit.Infrastructure/Git/GitLab/GitLabCommentResponder.cs
using System.Net.Http.Json;
using System.Text.Json;

namespace Naudit.Infrastructure.Git.GitLab;

/// <summary>GitLab-Umsetzung des FP-Antwort-Kommandos. Autorisierung über die effektive
/// Mitgliedschaft (members/all, Access-Level ≥ Developer); Bestätigung als Note in der Discussion.</summary>
public sealed class GitLabCommentResponder(HttpClient http, IGitTokenProvider tokens) : IReviewCommentResponder
{
    private const int DeveloperAccessLevel = 30;

    public async Task<bool> IsAuthorizedAsync(ReviewCommentReply reply, CancellationToken ct = default)
    {
        if (reply.AuthorId is not long userId)
            return false;   // ohne Autor-Id nicht verifizierbar ⇒ fail-closed

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"api/v4/projects/{reply.ProjectId}/members/all/{userId}");
        req.Headers.Add("PRIVATE-TOKEN", await tokens.ResolveTokenAsync(reply.ProjectId, ct));
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            return false;   // 404 = kein Mitglied, alles andere = im Zweifel nein (fail-closed)

        try
        {
            var member = await res.Content.ReadFromJsonAsync<GitLabMember>(ct);
            return member is { AccessLevel: >= DeveloperAccessLevel };
        }
        catch (JsonException)
        {
            return false;   // leerer/unerwarteter Body ⇒ fail-closed
        }
    }

    public async Task PostReplyAsync(ReviewCommentReply reply, string body, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"api/v4/projects/{reply.ProjectId}/merge_requests/{reply.MergeRequestIid}/discussions/{reply.ReplyToCommentId}/notes");
        req.Headers.Add("PRIVATE-TOKEN", await tokens.ResolveTokenAsync(reply.ProjectId, ct));
        req.Content = JsonContent.Create(new { body });
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }
}
