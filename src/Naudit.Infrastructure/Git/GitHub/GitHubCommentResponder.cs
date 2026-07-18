// src/Naudit.Infrastructure/Git/GitHub/GitHubCommentResponder.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Naudit.Infrastructure.Git.GitHub;

/// <summary>GitHub-Umsetzung des FP-Antwort-Kommandos. Autorisierung ganz aus der Payload
/// (author_association — kein API-Call); Bestätigung als Reply auf den Review-Kommentar.</summary>
public sealed class GitHubCommentResponder(HttpClient http, IGitTokenProvider tokens) : IReviewCommentResponder
{
    // Wer als OWNER/MEMBER/COLLABORATOR kommentiert, gehört zum Repo — fail-closed für alles andere.
    private static readonly HashSet<string> Allowed =
        new(StringComparer.OrdinalIgnoreCase) { "OWNER", "MEMBER", "COLLABORATOR" };

    public Task<bool> IsAuthorizedAsync(ReviewCommentReply reply, CancellationToken ct = default)
        => Task.FromResult(reply.AuthorAssociation is { } a && Allowed.Contains(a));

    public async Task PostReplyAsync(ReviewCommentReply reply, string body, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"repos/{reply.ProjectId}/pulls/{reply.MergeRequestIid}/comments/{reply.ReplyToCommentId}/replies");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.ResolveTokenAsync(reply.ProjectId, ct));
        req.Content = JsonContent.Create(new { body });
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }
}
