using System.Net;
using System.Text;
using Naudit.Infrastructure.Setup;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>Webhook-Anlage per GitLab-API: Ergebnis pro Ziel, Teilerfolge okay,
/// idempotent (URL schon vorhanden ⇒ skip), wirft nie.</summary>
public sealed class GitLabHookCreatorTests
{
    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Projekt_legtHookAn_mitTokenUndMrEvents()
    {
        var stub = new StubHttpMessageHandler(req =>
            req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, "[]") : Json(HttpStatusCode.Created, "{}"));
        var creator = new GitLabHookCreator(new HttpClient(stub));

        var results = await creator.CreateAsync("https://gitlab.example.com/", "glpat-x",
            "https://naudit.example.com/webhook/gitlab", "hook-secret",
            [new GitLabHookTarget(GitLabHookTargetKind.Project, "42")]);

        Assert.True(results.Single().Ok);
        var post = stub.Calls.Single(c => c.Method == HttpMethod.Post);
        Assert.Equal("https://gitlab.example.com/api/v4/projects/42/hooks", post.Uri!.ToString());
        Assert.Contains("\"merge_requests_events\":true", post.Body);
        Assert.Contains("\"push_events\":false", post.Body);
        Assert.Contains("hook-secret", post.Body);
        Assert.Contains("https://naudit.example.com/webhook/gitlab", post.Body);
        Assert.Equal("glpat-x", stub.LastRequest!.Headers.GetValues("PRIVATE-TOKEN").Single());
    }

    [Fact]
    public async Task Gruppe_nutztGroupsPfad_undUrlCodiert()
    {
        var stub = new StubHttpMessageHandler(req =>
            req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, "[]") : Json(HttpStatusCode.Created, "{}"));
        await new GitLabHookCreator(new HttpClient(stub)).CreateAsync("https://gitlab.example.com", "t",
            "https://n.example/webhook/gitlab", "s",
            [new GitLabHookTarget(GitLabHookTargetKind.Group, "my-group/sub")]);
        var post = stub.Calls.Single(c => c.Method == HttpMethod.Post);
        Assert.Equal("https://gitlab.example.com/api/v4/groups/my-group%2Fsub/hooks", post.Uri!.ToString());
    }

    [Fact]
    public async Task VorhandenerHook_wirdUebersprungen()
    {
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """[{"id":1,"url":"https://naudit.example.com/webhook/gitlab"}]"""));
        var results = await new GitLabHookCreator(new HttpClient(stub)).CreateAsync(
            "https://gitlab.example.com", "t", "https://naudit.example.com/webhook/gitlab", "s",
            [new GitLabHookTarget(GitLabHookTargetKind.Project, "42")]);
        Assert.True(results.Single().Ok);
        Assert.Contains("already exists", results.Single().Detail);
        Assert.DoesNotContain(stub.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Fehler403Und404_werdenProZielGemappt()
    {
        var stub = new StubHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/groups/")
                ? Json(HttpStatusCode.Forbidden, "{}")
                : req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, "[]") : Json(HttpStatusCode.NotFound, "{}"));
        var results = await new GitLabHookCreator(new HttpClient(stub)).CreateAsync(
            "https://gitlab.example.com", "t", "https://n.example/webhook/gitlab", "s",
            [new GitLabHookTarget(GitLabHookTargetKind.Project, "99"),
             new GitLabHookTarget(GitLabHookTargetKind.Group, "grp")]);

        var project = results.Single(r => r.Target.Kind == GitLabHookTargetKind.Project);
        Assert.False(project.Ok);
        Assert.Equal(404, project.Status);
        var group = results.Single(r => r.Target.Kind == GitLabHookTargetKind.Group);
        Assert.False(group.Ok);
        Assert.Equal(403, group.Status);
        Assert.Contains("Premium", group.Detail); // Gruppen-Hooks sind teils Premium-Tier (Spec-Hinweis)
    }

    [Fact]
    public async Task Netzwerkfehler_istErgebnisKeineException()
    {
        var stub = new StubHttpMessageHandler(_ => throw new HttpRequestException("no route to host"));
        var results = await new GitLabHookCreator(new HttpClient(stub)).CreateAsync(
            "https://gitlab.example.com", "t", "https://n.example/webhook/gitlab", "s",
            [new GitLabHookTarget(GitLabHookTargetKind.Project, "1")]);
        Assert.False(results.Single().Ok);
        Assert.Contains("no route to host", results.Single().Detail);
    }

    [Fact]
    public async Task KaputterJsonBody_istErgebnisKeineException()
    {
        // GET 200 mit unparsebarem Body: JsonException darf nicht durchschlagen (wirft nie).
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "kein json"));
        var results = await new GitLabHookCreator(new HttpClient(stub)).CreateAsync(
            "https://gitlab.example.com", "t", "https://n.example/webhook/gitlab", "s",
            [new GitLabHookTarget(GitLabHookTargetKind.Project, "1")]);
        Assert.False(results.Single().Ok);
        Assert.Contains("Invalid response", results.Single().Detail);
    }

    [Fact]
    public async Task Timeout_istErgebnisKeineException()
    {
        // HttpClient-Timeout ⇒ TaskCanceledException (kein HttpRequestException) — darf nicht werfen.
        var stub = new StubHttpMessageHandler(_ => throw new TaskCanceledException("timeout"));
        var results = await new GitLabHookCreator(new HttpClient(stub)).CreateAsync(
            "https://gitlab.example.com", "t", "https://n.example/webhook/gitlab", "s",
            [new GitLabHookTarget(GitLabHookTargetKind.Project, "1")]);
        Assert.False(results.Single().Ok);
        Assert.Contains("timed out", results.Single().Detail);
    }

    [Fact]
    public async Task ExpliziteCancellation_wirdNichtAlsTimeoutVerschluckt()
    {
        // Caller-Cancellation bleibt Cancellation — nicht als "timed out"-Ergebnis geschluckt.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stub = new StubHttpMessageHandler(_ => throw new TaskCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GitLabHookCreator(new HttpClient(stub)).CreateAsync(
                "https://gitlab.example.com", "t", "https://n.example/webhook/gitlab", "s",
                [new GitLabHookTarget(GitLabHookTargetKind.Project, "1")], cts.Token));
    }
}
