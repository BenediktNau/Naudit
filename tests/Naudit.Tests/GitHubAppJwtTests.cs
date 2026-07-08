using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Naudit.Infrastructure.Git.GitHub;
using Xunit;

namespace Naudit.Tests;

public class GitHubAppJwtTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void Create_producesVerifiableRs256Jwt_withIssuerAndExpiry()
    {
        using var rsa = RSA.Create(2048);
        using var jwt = new GitHubAppJwt("12345", rsa.ExportRSAPrivateKeyPem(), new FakeTime(T0));

        var token = jwt.Create();

        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1]));
        Assert.Contains("\"iss\":\"12345\"", payload);
        // exp = now + 9 min (< GitHub-Maximum 10 min), iat = now - 60 s.
        Assert.Contains($"\"exp\":{T0.AddMinutes(9).ToUnixTimeSeconds()}", payload);
        Assert.Contains($"\"iat\":{T0.AddSeconds(-60).ToUnixTimeSeconds()}", payload);
        // Echte RS256-Signatur mit dem Public Key verifizieren.
        Assert.True(rsa.VerifyData(
            Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Create_acceptsBase64EncodedPem()
    {
        using var rsa = RSA.Create(2048);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportRSAPrivateKeyPem()));
        using var jwt = new GitHubAppJwt("12345", b64, new FakeTime(T0));

        var parts = jwt.Create().Split('.');
        Assert.True(rsa.VerifyData(
            Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}
