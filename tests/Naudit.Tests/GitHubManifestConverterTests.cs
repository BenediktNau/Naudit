using System.Net;
using Naudit.Infrastructure.Setup;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>Exchange POST /app-manifests/{code}/conversions — braucht keine Auth,
/// der kurzlebige Code ist das Credential. 201 ⇒ App-Credentials, sonst Exception.</summary>
public sealed class GitHubManifestConverterTests
{
    private const string ConversionJson = """
        { "id": 4711, "slug": "naudit-test",
          "pem": "-----BEGIN RSA PRIVATE KEY-----\nX\n-----END RSA PRIVATE KEY-----",
          "webhook_secret": "hook-geheim", "client_id": "Iv1.x",
          "html_url": "https://github.com/apps/naudit-test" }
        """;

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body) };

    [Fact]
    public async Task Convert_ruftConversionsEndpoint_undMapptFelder()
    {
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.Created, ConversionJson));
        var converter = new GitHubManifestConverter(new HttpClient(stub));

        var result = await converter.ConvertAsync("https://github.com", "code-123");

        Assert.Equal("https://api.github.com/app-manifests/code-123/conversions",
            stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest.Method);
        Assert.Equal("4711", result.AppId);
        Assert.StartsWith("-----BEGIN RSA", result.PrivateKey);
        Assert.Equal("hook-geheim", result.WebhookSecret);
        Assert.Equal("naudit-test", result.Slug);
    }

    [Fact]
    public async Task Convert_Ghes_nutztApiV3()
    {
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.Created, ConversionJson));
        await new GitHubManifestConverter(new HttpClient(stub)).ConvertAsync("https://ghes.example.com/", "c");
        Assert.Equal("https://ghes.example.com/api/v3/app-manifests/c/conversions",
            stub.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Convert_nicht201_wirft()
    {
        // Abgelaufener/ungueltiger Code: GitHub antwortet 404 — Fehler im Schritt, Draft bleibt (Spec).
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubManifestConverter(new HttpClient(stub)).ConvertAsync("https://github.com", "alt"));
    }

    [Fact]
    public async Task Convert_ohneWebhookSecret_wirft()
    {
        // Fail-closed: ohne webhook_secret kann Naudit die HMAC-Signatur nicht pruefen.
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.Created,
            """{ "id": 1, "slug": "s", "pem": "PEM", "webhook_secret": null }"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubManifestConverter(new HttpClient(stub)).ConvertAsync("https://github.com", "c"));
    }

    [Fact]
    public async Task Convert_201_aberKaputterBody_wirftInvalidOperation()
    {
        // 201 mit unparsebarem Body: JsonException wird zu InvalidOperationException (Contract).
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.Created, "kein json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubManifestConverter(new HttpClient(stub)).ConvertAsync("https://github.com", "c"));
    }
}
