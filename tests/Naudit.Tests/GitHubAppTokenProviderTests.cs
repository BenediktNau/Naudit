using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitHubAppTokenProviderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingLogger : ILogger<GitHubAppTokenProvider>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
            => Messages.Add(fmt(state, ex));
    }

    // Stub-API: GET …/installation → {"id":123}; POST …/access_tokens → Token mit expires_at.
    private static StubHttpMessageHandler AppApi(Func<DateTimeOffset> expiresAt)
        => new(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var json = path.EndsWith("/installation")
                ? """{"id":123}"""
                : $$"""{"token":"ghs_test","expires_at":"{{expiresAt():O}}"}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

    private static GitHubAppTokenProvider Provider(
        RSA rsa, StubHttpMessageHandler stub, TimeProvider time, ILogger<GitHubAppTokenProvider>? log = null,
        long? installationId = null, bool base64Pem = false)
    {
        var pem = rsa.ExportRSAPrivateKeyPem();
        var options = new GitHubAppOptions
        {
            AppId = "12345",
            PrivateKey = base64Pem ? Convert.ToBase64String(Encoding.UTF8.GetBytes(pem)) : pem,
            InstallationId = installationId,
        };
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.github.com/") };
        return new GitHubAppTokenProvider(http, options, log ?? new CapturingLogger(), time);
    }

    [Fact]
    public async Task ResolveTokenAsync_mintsInstallationToken_viaJwtAndLookup()
    {
        using var rsa = RSA.Create(2048);
        var stub = AppApi(() => T0.AddHours(1));
        var provider = Provider(rsa, stub, new FakeTime(T0));

        var token = await provider.ResolveTokenAsync("octo/hello-world");

        Assert.Equal("ghs_test", token);
        // Call 1 = Installation-Lookup, Call 2 = Token-Mint — beide mit JWT (Bearer, 3 Segmente).
        Assert.Equal(2, stub.Calls.Count);
        Assert.EndsWith("/repos/octo/hello-world/installation", stub.Calls[0].Uri!.AbsolutePath);
        Assert.EndsWith("/app/installations/123/access_tokens", stub.Calls[1].Uri!.AbsolutePath);
        var jwt = stub.Requests[0].Headers.Authorization!;
        Assert.Equal("Bearer", jwt.Scheme);
        var parts = jwt.Parameter!.Split('.');
        Assert.Equal(3, parts.Length);
        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1]));
        Assert.Contains("\"iss\":\"12345\"", payload);
        // Signatur mit dem Public Key verifizieren (echtes RS256, kein Fake-JWT).
        Assert.True(rsa.VerifyData(
            Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task ResolveTokenAsync_cachesToken_andRefreshesShortlyBeforeExpiry()
    {
        using var rsa = RSA.Create(2048);
        var time = new FakeTime(T0);
        var stub = AppApi(() => time.Now.AddHours(1));
        var provider = Provider(rsa, stub, time);

        await provider.ResolveTokenAsync("octo/hello-world");
        await provider.ResolveTokenAsync("octo/hello-world");
        // 2. Aufruf aus dem Cache: weiterhin nur 1 Lookup + 1 Mint.
        Assert.Equal(2, stub.Calls.Count);

        // Bis kurz vor Ablauf (Refresh-Fenster 5 min) bleibt der Cache gültig …
        time.Now = T0.AddMinutes(54);
        await provider.ResolveTokenAsync("octo/hello-world");
        Assert.Equal(2, stub.Calls.Count);

        // … danach wird neu gemintet (Installation-Id bleibt gecached: nur +1 Mint, kein 2. Lookup).
        time.Now = T0.AddMinutes(56);
        await provider.ResolveTokenAsync("octo/hello-world");
        Assert.Equal(3, stub.Calls.Count);
        Assert.EndsWith("/access_tokens", stub.Calls[2].Uri!.AbsolutePath);
    }

    [Fact]
    public async Task ResolveTokenAsync_usesConfiguredInstallationId_withoutLookup()
    {
        using var rsa = RSA.Create(2048);
        var stub = AppApi(() => T0.AddHours(1));
        var provider = Provider(rsa, stub, new FakeTime(T0), installationId: 777);

        await provider.ResolveTokenAsync("octo/hello-world");

        var call = Assert.Single(stub.Calls);
        Assert.EndsWith("/app/installations/777/access_tokens", call.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task ResolveTokenAsync_acceptsBase64EncodedPem()
    {
        using var rsa = RSA.Create(2048);
        var stub = AppApi(() => T0.AddHours(1));
        var provider = Provider(rsa, stub, new FakeTime(T0), base64Pem: true);

        Assert.Equal("ghs_test", await provider.ResolveTokenAsync("octo/hello-world"));
    }

    [Fact]
    public async Task ResolveTokenAsync_notInstalled_throwsWithProjectId()
    {
        using var rsa = RSA.Create(2048);
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var provider = Provider(rsa, stub, new FakeTime(T0));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ResolveTokenAsync("octo/hello-world").AsTask());
        Assert.Contains("octo/hello-world", ex.Message);
    }

    [Fact]
    public async Task ResolveTokenAsync_staleInstallationId_relooksUpOnceAndMintsFresh()
    {
        // App deinstalliert + neu installiert ⇒ neue Installation-Id. Der Mint gegen die alte Id
        // liefert 404; der Provider muss den Installation-Cache verwerfen und einmal frisch auflösen
        // (statt bis zum Prozess-Neustart gegen die tote Id zu laufen).
        using var rsa = RSA.Create(2048);
        var time = new FakeTime(T0);
        var reinstalled = false;
        var stub = new StubHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            string json;
            if (path.EndsWith("/installation"))
                json = reinstalled ? """{"id":456}""" : """{"id":123}""";
            else if (path.Contains("/installations/123/"))
            {
                if (reinstalled)
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                json = $$"""{"token":"ghs_old","expires_at":"{{T0.AddHours(1):O}}"}""";
            }
            else
                json = $$"""{"token":"ghs_new","expires_at":"{{time.Now.AddHours(1):O}}"}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        var provider = Provider(rsa, stub, time);

        Assert.Equal("ghs_old", await provider.ResolveTokenAsync("octo/hello-world"));

        reinstalled = true;
        time.Now = T0.AddHours(2); // altes Token abgelaufen ⇒ Neu-Mint gegen die (jetzt tote) Id 123
        Assert.Equal("ghs_new", await provider.ResolveTokenAsync("octo/hello-world"));

        // Reihenfolge: Lookup(123), Mint(123), Mint(123)=404, Lookup(456), Mint(456).
        Assert.Equal(5, stub.Calls.Count);
        Assert.EndsWith("/app/installations/456/access_tokens", stub.Calls[4].Uri!.AbsolutePath);
    }

    [Fact]
    public async Task Minting_neverLogsPrivateKeyOrToken()
    {
        using var rsa = RSA.Create(2048);
        var log = new CapturingLogger();
        var stub = AppApi(() => T0.AddHours(1));
        var provider = Provider(rsa, stub, new FakeTime(T0), log);

        await provider.ResolveTokenAsync("octo/hello-world");

        Assert.DoesNotContain(log.Messages, m => m.Contains("ghs_test") || m.Contains("PRIVATE KEY"));
    }
}
