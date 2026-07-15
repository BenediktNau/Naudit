using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git.GitLab;

/// <summary>GitLab-Autor-Auflösung: die Webhook-Payload trägt nur author_id, deshalb EIN
/// GET auf den MR (liefert author.username). Läuft nur, wenn Autor-Sessions aktiv sind
/// und der Login nicht schon im Request steht (POST /review kann ihn mitgeben).</summary>
public sealed class GitLabAuthorLoginResolver(
    HttpClient http, IGitTokenProvider tokens, ILogger<GitLabAuthorLoginResolver> logger) : IAuthorLoginResolver
{
    public async Task<string?> ResolveAsync(ReviewRequest request, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(request.AuthorLogin))
            return request.AuthorLogin;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"api/v4/projects/{request.ProjectId}/merge_requests/{request.MergeRequestIid}");
            req.Headers.Add("PRIVATE-TOKEN", await tokens.ResolveTokenAsync(request.ProjectId, ct));
            using var response = await http.SendAsync(req, ct);
            response.EnsureSuccessStatusCode();
            var detail = await response.Content.ReadFromJsonAsync<GitLabMergeRequestDetail>(ct);
            return detail?.Author?.Username;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // fail-quiet: ohne Autor läuft das Review über den globalen Provider.
            logger.LogWarning(ex, "GitLab-Autor-Auflösung für {Project}!{Iid} fehlgeschlagen.",
                request.ProjectId, request.MergeRequestIid);
            return null;
        }
    }
}
