using System.Text.Json.Serialization;

namespace Naudit.Infrastructure.Setup;

/// <summary>Das GitHub-App-Manifest, wie GitHub es im Form-Feld "manifest" erwartet
/// (snake_case per JsonPropertyName — auch wenn der Host camelCase serialisiert).</summary>
public sealed record GitHubAppManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("hook_attributes")] GitHubAppManifestHook HookAttributes,
    [property: JsonPropertyName("redirect_url")] string RedirectUrl,
    [property: JsonPropertyName("public")] bool Public,
    [property: JsonPropertyName("default_permissions")] IReadOnlyDictionary<string, string> DefaultPermissions,
    [property: JsonPropertyName("default_events")] IReadOnlyList<string> DefaultEvents,
    [property: JsonPropertyName("description")] string Description);

public sealed record GitHubAppManifestHook(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("active")] bool Active = true);

/// <summary>Baut Manifest und URLs fuer den GitHub-App-Manifest-Flow. Reine Funktionen —
/// der Browser POSTet das Manifest an {WebHost}/settings/apps/new, GitHub redirected mit
/// ?code= zurueck (Exchange macht GitHubManifestConverter). GHES: API liegt unter /api/v3.</summary>
public static class GitHubManifest
{
    public const string DefaultWebHost = "https://github.com";

    /// <summary>Leer ⇒ github.com; sonst Host getrimmt, ohne Slash am Ende.</summary>
    public static string Normalize(string? webHost) =>
        string.IsNullOrWhiteSpace(webHost) ? DefaultWebHost : webHost.Trim().TrimEnd('/');

    /// <summary>github.com ⇒ api.github.com; GHES ⇒ {host}/api/v3.</summary>
    public static string ApiBase(string? webHost)
    {
        var host = Normalize(webHost);
        return host == DefaultWebHost ? "https://api.github.com" : $"{host}/api/v3";
    }

    public static string CreateAppUrl(string? webHost, string? org, string state)
    {
        var path = string.IsNullOrWhiteSpace(org)
            ? "/settings/apps/new"
            : $"/organizations/{Uri.EscapeDataString(org.Trim())}/settings/apps/new";
        return $"{Normalize(webHost)}{path}?state={Uri.EscapeDataString(state)}";
    }

    public static string InstallUrl(string? webHost, string slug) =>
        $"{Normalize(webHost)}/apps/{Uri.EscapeDataString(slug)}/installations/new";

    /// <summary>Permissions/Events entsprechen dem, was Naudit braucht: PRs kommentieren
    /// (pull_requests:write), Code lesen (contents:read), Event pull_request (Spec).</summary>
    public static GitHubAppManifest Build(string publicBaseUrl, string appName, bool isPublic)
    {
        var baseUrl = publicBaseUrl.TrimEnd('/');
        return new GitHubAppManifest(
            Name: appName,
            Url: baseUrl,
            HookAttributes: new GitHubAppManifestHook($"{baseUrl}/webhook/github"),
            RedirectUrl: $"{baseUrl}/api/setup/github/manifest-callback",
            Public: isPublic,
            DefaultPermissions: new Dictionary<string, string> { ["pull_requests"] = "write", ["contents"] = "read" },
            DefaultEvents: ["pull_request"],
            Description: "Naudit code review bot");
    }
}
