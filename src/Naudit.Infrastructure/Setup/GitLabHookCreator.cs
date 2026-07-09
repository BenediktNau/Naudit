using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Naudit.Infrastructure.Setup;

public enum GitLabHookTargetKind { Project, Group }

public sealed record GitLabHookTarget(GitLabHookTargetKind Kind, string IdOrPath);

public sealed record GitLabHookResult(GitLabHookTarget Target, bool Ok, int? Status, string Detail);

/// <summary>Legt GitLab-Webhooks per API an (Wizard-Plattform-Automation). Teilerfolge sind
/// gewollt: jedes Ziel bekommt sein eigenes Ergebnis, es wird nie geworfen. Idempotent:
/// existiert am Ziel schon ein Hook mit derselben URL, wird keiner angelegt (Retry-freundlich).</summary>
public sealed class GitLabHookCreator(HttpClient http)
{
    public async Task<IReadOnlyList<GitLabHookResult>> CreateAsync(string baseUrl, string token,
        string webhookUrl, string secret, IReadOnlyList<GitLabHookTarget> targets, CancellationToken ct = default)
    {
        var results = new List<GitLabHookResult>(targets.Count);
        foreach (var target in targets)
            results.Add(await CreateOneAsync(baseUrl.TrimEnd('/'), token, webhookUrl, secret, target, ct));
        return results;
    }

    private async Task<GitLabHookResult> CreateOneAsync(string baseUrl, string token,
        string webhookUrl, string secret, GitLabHookTarget target, CancellationToken ct)
    {
        var scope = target.Kind == GitLabHookTargetKind.Project ? "projects" : "groups";
        var hooksUrl = $"{baseUrl}/api/v4/{scope}/{Uri.EscapeDataString(target.IdOrPath)}/hooks";
        try
        {
            // Idempotenz-Check: Nicht-200 hier ist egal — dann entscheidet der POST.
            using (var listReq = NewRequest(HttpMethod.Get, hooksUrl, token))
            using (var listRes = await http.SendAsync(listReq, ct))
            {
                if (listRes.StatusCode == HttpStatusCode.OK)
                {
                    var hooks = JsonSerializer.Deserialize<List<HookDto>>(await listRes.Content.ReadAsStringAsync(ct));
                    if (hooks?.Any(h => h.Url == webhookUrl) == true)
                        return new(target, true, 200, "A webhook with this URL already exists — skipped.");
                }
            }

            using var req = NewRequest(HttpMethod.Post, hooksUrl, token);
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                url = webhookUrl,
                token = secret,
                merge_requests_events = true,
                push_events = false,
            }), Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(req, ct);
            return (int)res.StatusCode switch
            {
                201 => new(target, true, 201, "Webhook created."),
                401 or 403 => new(target, false, (int)res.StatusCode,
                    target.Kind == GitLabHookTargetKind.Group
                        ? "Access denied — the token lacks permission (group webhooks may require GitLab Premium)."
                        : "Access denied — the token lacks permission for this project."),
                404 => new(target, false, 404, "Not found — check the ID or path."),
                var s => new(target, false, s, $"GitLab answered {s}."),
            };
        }
        catch (HttpRequestException ex)
        {
            return new(target, false, null, $"Network error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return new(target, false, null, $"Invalid response from GitLab: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient-Timeout — nicht die Caller-Cancellation (die bleibt Cancellation).
            return new(target, false, null, $"Request timed out: {ex.Message}");
        }
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("PRIVATE-TOKEN", token);
        return req;
    }

    private sealed record HookDto([property: JsonPropertyName("url")] string? Url);
}
