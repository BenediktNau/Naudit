using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Naudit.Infrastructure.Git.GitHub;

/// <summary>IGitTokenProvider für den GitHub-App-Modus: mintet kurzlebige Installation-Tokens
/// (App-JWT → Installation-Lookup → access_tokens) und cached sie bis kurz vor Ablauf.
/// Private Key und gemintete Tokens dürfen NIE geloggt werden.</summary>
public sealed class GitHubAppTokenProvider(
    HttpClient http, GitHubAppOptions options, ILogger<GitHubAppTokenProvider> logger, TimeProvider? time = null)
    : IGitTokenProvider, IDisposable
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);   // vor Ablauf erneuern
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);                          // serialisiert nur Lookup/Mint, nicht den Cache-Hit
    // Key einmal importieren (er ändert sich nie); signiert wird nur unter _gate,
    // weil RSA-Instanzmethoden nicht thread-safe garantiert sind.
    private readonly RSA _rsa = ImportPrivateKey(options.PrivateKey);
    private readonly ConcurrentDictionary<string, long> _installations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, (string Token, DateTimeOffset ExpiresAt)> _tokens = new();

    public async ValueTask<string> ResolveTokenAsync(string projectId, CancellationToken ct = default)
    {
        // Schneller Pfad ohne Lock: gültiges Token liegt im Cache (Normalfall zwischen zwei Mints).
        if (TryGetCached(projectId, out var cached))
            return cached;

        await _gate.WaitAsync(ct);
        try
        {
            // Doppelt geprüft: ein parallel wartender Request kann inzwischen gemintet haben.
            if (TryGetCached(projectId, out cached))
                return cached;

            var installationId = options.InstallationId ?? await GetInstallationIdAsync(projectId, ct);
            var minted = await MintAsync(installationId, ct);
            if (minted is null && options.InstallationId is null)
            {
                // Installation-Id war veraltet (App neu installiert / Repo transferiert):
                // Cache verwerfen und genau einmal frisch auflösen statt bis zum Neustart 404 zu laufen.
                _installations.TryRemove(projectId, out _);
                installationId = await GetInstallationIdAsync(projectId, ct);
                minted = await MintAsync(installationId, ct);
            }
            if (minted is not { } fresh)
                throw new InvalidOperationException(
                    $"GitHub-Installation {installationId} liefert kein Token (404) — ist die App (AppId {options.AppId}) dort noch installiert?");

            _tokens[installationId] = fresh;
            // Bewusst nur Metadaten loggen — nie Token oder Key.
            logger.LogInformation("GitHub-App-Installation-Token erneuert (Installation {InstallationId}, gültig bis {ExpiresAt:O}).",
                installationId, fresh.ExpiresAt);
            return fresh.Token;
        }
        finally { _gate.Release(); }
    }

    private bool TryGetCached(string projectId, out string token)
    {
        token = string.Empty;
        long? id = options.InstallationId;
        if (id is null && _installations.TryGetValue(projectId, out var known))
            id = known;
        if (id is { } installationId
            && _tokens.TryGetValue(installationId, out var cached)
            && _time.GetUtcNow() < cached.ExpiresAt - RefreshSkew)
        {
            token = cached.Token;
            return true;
        }
        return false;
    }

    private async Task<long> GetInstallationIdAsync(string projectId, CancellationToken ct)
    {
        if (_installations.TryGetValue(projectId, out var known))
            return known;

        using var resp = await SendWithJwtAsync(HttpMethod.Get, $"repos/{projectId}/installation", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"Die GitHub App (AppId {options.AppId}) ist im Repo '{projectId}' nicht installiert — erst \"Install app\" ausführen.");
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<InstallationDto>(ct)
            ?? throw new InvalidOperationException("GitHub lieferte keine Installation.");
        _installations[projectId] = dto.Id;
        return dto.Id;
    }

    /// <summary>Mintet ein Installation-Token; null bei 404 (Installation existiert nicht mehr).</summary>
    private async Task<(string Token, DateTimeOffset ExpiresAt)?> MintAsync(long installationId, CancellationToken ct)
    {
        using var resp = await SendWithJwtAsync(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<InstallationTokenDto>(ct);
        if (dto?.Token is not { Length: > 0 })
            throw new InvalidOperationException("GitHub lieferte kein Installation-Token.");
        return (dto.Token, dto.ExpiresAt);
    }

    private async Task<HttpResponseMessage> SendWithJwtAsync(HttpMethod method, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateJwt());
        return await http.SendAsync(req, ct);
    }

    // App-JWT: RS256, iat 60 s rückdatiert (Clock-Skew), exp 9 min (< GitHub-Maximum 10 min).
    private string CreateJwt()
    {
        var now = _time.GetUtcNow();
        var header = """{"alg":"RS256","typ":"JWT"}""";
        var payload = $$"""{"iat":{{now.AddSeconds(-60).ToUnixTimeSeconds()}},"exp":{{now.AddMinutes(9).ToUnixTimeSeconds()}},"iss":"{{options.AppId}}"}""";
        var signingInput = $"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header))}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))}";
        var signature = _rsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    private static RSA ImportPrivateKey(string key)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(LoadPem(key));
        return rsa;
    }

    // Env-freundlich: roher PEM-Text ODER Base64-codiertes PEM (Coolify/Docker-Env ohne Zeilenumbrüche).
    private static string LoadPem(string key)
        => key.Contains("-----BEGIN", StringComparison.Ordinal)
            ? key
            : Encoding.UTF8.GetString(Convert.FromBase64String(key));

    public void Dispose()
    {
        _rsa.Dispose();
        _gate.Dispose();
    }

    private sealed record InstallationDto([property: JsonPropertyName("id")] long Id);
    private sealed record InstallationTokenDto(
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
}
