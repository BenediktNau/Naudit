using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Naudit.Infrastructure.Setup;

/// <summary>Ergebnis der Manifest-Conversion: alles, was der Draft fuer Auth=App braucht.</summary>
public sealed record GitHubManifestConversion(string AppId, string PrivateKey, string WebhookSecret, string Slug);

/// <summary>Tauscht den Manifest-Code: POST {api}/app-manifests/{code}/conversions.
/// Braucht KEINE Auth (der ~1h gueltige Code ist das Credential) — funktioniert also auch,
/// bevor Naudit oeffentlich erreichbar ist. Jeder Fehler ⇒ InvalidOperationException,
/// der Callback-Endpoint uebersetzt das in einen Redirect mit Fehler-Flag.</summary>
public sealed class GitHubManifestConverter(HttpClient http)
{
    public async Task<GitHubManifestConversion> ConvertAsync(string? webHost, string code, CancellationToken ct = default)
    {
        var url = $"{GitHubManifest.ApiBase(webHost)}/app-manifests/{Uri.EscapeDataString(code)}/conversions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("User-Agent", "Naudit");
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException($"GitHub manifest conversion failed ({(int)res.StatusCode}).");

        var dto = JsonSerializer.Deserialize<ConversionDto>(body)
            ?? throw new InvalidOperationException("GitHub manifest conversion returned an empty body.");
        if (dto.Id <= 0 || string.IsNullOrWhiteSpace(dto.Pem)
            || string.IsNullOrWhiteSpace(dto.WebhookSecret) || string.IsNullOrWhiteSpace(dto.Slug))
            throw new InvalidOperationException("GitHub manifest conversion returned incomplete app credentials.");

        return new GitHubManifestConversion(
            dto.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            dto.Pem, dto.WebhookSecret, dto.Slug);
    }

    private sealed record ConversionDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("slug")] string? Slug,
        [property: JsonPropertyName("pem")] string? Pem,
        [property: JsonPropertyName("webhook_secret")] string? WebhookSecret);
}
