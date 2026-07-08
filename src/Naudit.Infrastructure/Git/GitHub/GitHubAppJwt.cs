using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Naudit.Infrastructure.Git.GitHub;

/// <summary>Erzeugt kurzlebige App-JWTs (RS256) für die GitHub-App-Authentifizierung. Von
/// <see cref="GitHubAppTokenProvider"/> und <see cref="GitHubAppInstallationChecker"/> geteilt.
/// Der private Key wird EINMAL importiert; das Signieren läuft unter einem Lock (RSA-Instanzmethoden
/// sind nicht garantiert thread-safe). Key und JWT dürfen NIE geloggt werden.</summary>
public sealed class GitHubAppJwt : IDisposable
{
    private readonly RSA _rsa;
    private readonly string _appId;
    private readonly TimeProvider _time;
    private readonly object _sign = new();

    public GitHubAppJwt(string appId, string privateKey, TimeProvider? time = null)
    {
        _appId = appId;
        _rsa = ImportPrivateKey(privateKey);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>App-JWT: RS256, iat 60 s rückdatiert (Clock-Skew), exp 9 min (&lt; GitHub-Maximum 10 min).</summary>
    public string Create()
    {
        var now = _time.GetUtcNow();
        var header = """{"alg":"RS256","typ":"JWT"}""";
        var payload = $$"""{"iat":{{now.AddSeconds(-60).ToUnixTimeSeconds()}},"exp":{{now.AddMinutes(9).ToUnixTimeSeconds()}},"iss":"{{_appId}}"}""";
        var signingInput = $"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header))}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))}";
        byte[] signature;
        lock (_sign)
            signature = _rsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
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

    public void Dispose() => _rsa.Dispose();
}
