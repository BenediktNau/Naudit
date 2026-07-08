using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Naudit.Infrastructure.Git.GitHub;

/// <summary>Prüft für das WebUI-Onboarding, ob die Naudit-GitHub-App bei den verknüpften Logins
/// installiert ist, und liefert den Deep-Link zur Installation. Fail-quiet: API-Fehler ⇒ null
/// (Banner bleibt aus), nie werfend — der Review-Betrieb ist davon nie betroffen.</summary>
public interface IGitHubAppInstallationChecker
{
    ValueTask<GitHubInstallationStatus> GetStatusAsync(IReadOnlyList<string> logins, CancellationToken ct = default);
}

/// <summary>Deep-Link zur App-Installation + je Login der Status (null = nicht ermittelbar).</summary>
public sealed record GitHubInstallationStatus(string InstallUrl, IReadOnlyList<GitHubLoginInstallation> Accounts);

/// <summary><paramref name="Installed"/>: true = installiert, false = nicht installiert, null = Prüfung fehlgeschlagen.</summary>
public sealed record GitHubLoginInstallation(string Login, bool? Installed);

public sealed class GitHubAppInstallationChecker(
    HttpClient http, GitHubAppJwt jwt, ILogger<GitHubAppInstallationChecker> logger, TimeProvider? time = null)
    : IGitHubAppInstallationChecker
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (bool? Installed, DateTimeOffset ExpiresAt)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private volatile string? _slug;   // Slug ändert sich nie ⇒ Prozess-Lebensdauer cachen.

    public async ValueTask<GitHubInstallationStatus> GetStatusAsync(IReadOnlyList<string> logins, CancellationToken ct = default)
    {
        var slug = await GetSlugAsync(ct);
        var installUrl = slug is null ? "" : $"https://github.com/apps/{slug}/installations/new";
        var accounts = new List<GitHubLoginInstallation>(logins.Count);
        foreach (var login in logins)
            accounts.Add(new GitHubLoginInstallation(login, await IsInstalledAsync(login, ct)));
        return new GitHubInstallationStatus(installUrl, accounts);
    }

    private async ValueTask<bool?> IsInstalledAsync(string login, CancellationToken ct)
    {
        if (_cache.TryGetValue(login, out var c) && _time.GetUtcNow() < c.ExpiresAt)
            return c.Installed;
        var installed = await ProbeAsync(login, ct);
        // Nur echte Ergebnisse cachen; Fehler (null) beim nächsten Laden erneut versuchen.
        if (installed is not null)
            _cache[login] = (installed, _time.GetUtcNow() + CacheFor);
        return installed;
    }

    // GET /users/{login}/installation → 200 installiert / 404 nicht ⇒ dann Org-Fallback probieren.
    private async ValueTask<bool?> ProbeAsync(string login, CancellationToken ct)
    {
        try
        {
            using var userResp = await SendAsync($"users/{login}/installation", ct);
            if (userResp.StatusCode == HttpStatusCode.OK) return true;
            if (userResp.StatusCode != HttpStatusCode.NotFound) return LogAndNull(login, userResp.StatusCode);

            using var orgResp = await SendAsync($"orgs/{login}/installation", ct);
            if (orgResp.StatusCode == HttpStatusCode.OK) return true;
            if (orgResp.StatusCode == HttpStatusCode.NotFound) return false;
            return LogAndNull(login, orgResp.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "GitHub-App-Installationsprüfung für {Login} fehlgeschlagen.", login);
            return null;
        }
    }

    private async ValueTask<string?> GetSlugAsync(CancellationToken ct)
    {
        if (_slug is not null) return _slug;
        try
        {
            using var resp = await SendAsync("app", ct);
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning("GitHub-App-Slug-Abruf: unerwarteter Status {Status}.", (int)resp.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var slug = doc.RootElement.TryGetProperty("slug", out var s) ? s.GetString() : null;
            if (!string.IsNullOrEmpty(slug)) _slug = slug;
            return _slug;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "GitHub-App-Slug konnte nicht geladen werden.");
            return null;
        }
    }

    private bool? LogAndNull(string login, HttpStatusCode code)
    {
        logger.LogWarning("GitHub-App-Installationsprüfung für {Login}: unerwarteter Status {Status}.", login, (int)code);
        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt.Create());
        return await http.SendAsync(req, ct);
    }
}
