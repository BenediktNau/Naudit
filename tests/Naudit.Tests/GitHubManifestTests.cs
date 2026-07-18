using System.Text.Json;
using Naudit.Infrastructure.Setup;
using Xunit;

namespace Naudit.Tests;

/// <summary>Manifest-Bau + Host-Ableitung fuer den GitHub-App-Manifest-Flow.
/// Alles pur — HTTP macht erst der Converter (Task 2).</summary>
public sealed class GitHubManifestTests
{
    [Fact]
    public void Build_setztHookRedirectPermissionsEvents()
    {
        var m = GitHubManifest.Build("https://naudit.example.com/", "naudit", isPublic: false);
        Assert.Equal("naudit", m.Name);
        Assert.Equal("https://naudit.example.com", m.Url);
        Assert.Equal("https://naudit.example.com/webhook/github", m.HookAttributes.Url);
        Assert.True(m.HookAttributes.Active);
        Assert.Equal("https://naudit.example.com/api/setup/github/manifest-callback", m.RedirectUrl);
        Assert.False(m.Public);
        Assert.Equal("write", m.DefaultPermissions["pull_requests"]);
        Assert.Equal("read", m.DefaultPermissions["contents"]);
        Assert.Equal(["pull_request", "pull_request_review_comment"], m.DefaultEvents);
    }

    [Fact]
    public void Build_serialisiertSnakeCase()
    {
        // GitHub erwartet exakt diese Feldnamen im Form-Feld "manifest".
        var json = JsonSerializer.Serialize(GitHubManifest.Build("https://n.example", "naudit", isPublic: true));
        Assert.Contains("\"hook_attributes\"", json);
        Assert.Contains("\"redirect_url\"", json);
        Assert.Contains("\"default_permissions\"", json);
        Assert.Contains("\"default_events\"", json);
        Assert.Contains("\"public\":true", json);
    }

    [Fact]
    public void ApiBase_githubCom_vs_Ghes()
    {
        Assert.Equal("https://api.github.com", GitHubManifest.ApiBase(null));
        Assert.Equal("https://api.github.com", GitHubManifest.ApiBase("https://github.com/"));
        Assert.Equal("https://ghes.example.com/api/v3", GitHubManifest.ApiBase("https://ghes.example.com"));
    }

    [Fact]
    public void CreateAppUrl_mitUndOhneOrg()
    {
        Assert.Equal("https://github.com/settings/apps/new?state=abc",
            GitHubManifest.CreateAppUrl(null, null, "abc"));
        Assert.Equal("https://ghes.example.com/organizations/my-org/settings/apps/new?state=abc",
            GitHubManifest.CreateAppUrl("https://ghes.example.com/", " my-org ", "abc"));
    }

    [Fact]
    public void InstallUrl_ausSlug()
    {
        Assert.Equal("https://github.com/apps/naudit-test/installations/new",
            GitHubManifest.InstallUrl(null, "naudit-test"));
    }

    [Fact]
    public void Build_subscribesToReviewCommentEvent()
    {
        var manifest = GitHubManifest.Build("https://naudit.example.com", "Naudit", isPublic: false);
        Assert.Contains("pull_request", manifest.DefaultEvents);
        Assert.Contains("pull_request_review_comment", manifest.DefaultEvents);
    }
}
