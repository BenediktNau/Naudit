using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Naudit.Infrastructure.Git.GitHub;

/// <summary>Die GitHub App ist im Repo nicht installiert (<c>GET repos/{repo}/installation</c> = 404).
/// Eigener Typ, damit der PAT-Fallback gezielt NUR diesen Fall abfängt — JWT-, Netzwerk-, 5xx- und
/// Mint-Fehler propagieren weiter. Erbt von InvalidOperationException (Bestandsverhalten).</summary>
public sealed class GitHubAppNotInstalledException(string projectId, string appId)
    : InvalidOperationException(
        $"Die GitHub App (AppId {appId}) ist im Repo '{projectId}' nicht installiert — erst \"Install app\" ausführen "
        + "oder einen PAT-Fallback konfigurieren (Naudit:GitHub:Token bzw. Naudit:GitHub:ProjectTokens).")
{
    public string ProjectId { get; } = projectId;
}

/// <summary>IGitTokenProvider für <c>Auth=App</c> mit PAT-Fallback (Opt-in, s. <see cref="GitHubOptions.UsesAppWithPatFallback"/>).
/// Reihenfolge je Repo: 1. expliziter Per-Projekt-PAT (bewusster Override), 2. App-Installation-Token,
/// 3. globaler PAT — aber NUR, wenn die App dort nicht installiert ist; 4. sonst der alte "nicht installiert"-Fehler.
/// Tokens werden nie geloggt.</summary>
public sealed class GitHubAppWithPatFallbackTokenProvider : IGitTokenProvider, IDisposable
{
    // Negativ-Cache: Nicht-App-Repos fragen nicht bei jedem Request die Installation-API; eine spätere
    // App-Installation greift trotzdem binnen Minuten.
    internal static readonly TimeSpan NotInstalledTtl = TimeSpan.FromMinutes(5);

    private readonly IGitTokenProvider _app;
    private readonly string _globalToken;
    private readonly ConfiguredGitTokenProvider _projectTokens;   // Default "" ⇒ "kein Override"
    private readonly ILogger<GitHubAppWithPatFallbackTokenProvider> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _notInstalledUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.OrdinalIgnoreCase);

    public GitHubAppWithPatFallbackTokenProvider(
        IGitTokenProvider app, string globalToken, IEnumerable<ProjectTokenEntry> projectTokens,
        ILogger<GitHubAppWithPatFallbackTokenProvider> logger, TimeProvider? time = null)
    {
        _app = app;
        _globalToken = string.IsNullOrWhiteSpace(globalToken) ? "" : globalToken;
        _projectTokens = new ConfiguredGitTokenProvider("", projectTokens);
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async ValueTask<string> ResolveTokenAsync(string projectId, CancellationToken ct = default)
    {
        // 1. Expliziter Per-Projekt-PAT gewinnt immer (ohne App-Call).
        var projectToken = await _projectTokens.ResolveTokenAsync(projectId, ct);
        if (projectToken.Length > 0)
            return projectToken;

        // Negativ-Cache: kürzlich als "nicht installiert" erkannt ⇒ direkt globaler PAT.
        if (_notInstalledUntil.TryGetValue(projectId, out var until) && _time.GetUtcNow() < until)
            return _globalToken;

        try
        {
            // 2. App-Token; Erfolg räumt einen abgelaufenen Negativ-Eintrag weg.
            var token = await _app.ResolveTokenAsync(projectId, ct);
            _notInstalledUntil.TryRemove(projectId, out _);
            return token;
        }
        catch (GitHubAppNotInstalledException) when (_globalToken.Length > 0)
        {
            // 3. Nur "nicht installiert" fällt auf den globalen PAT zurück; ohne globalen PAT (4.) propagiert der Fehler.
            _notInstalledUntil[projectId] = _time.GetUtcNow() + NotInstalledTtl;
            if (_warned.TryAdd(projectId, 0))
                _logger.LogWarning(
                    "GitHub App ist im Repo {ProjectId} nicht installiert — nutze den PAT-Fallback (Naudit:GitHub:Token). "
                    + "Kommentare erscheinen dort unter dem PAT-Nutzer statt als Bot.", projectId);
            return _globalToken;
        }
    }

    public void Dispose() => (_app as IDisposable)?.Dispose();
}
