using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Infrastructure.Setup;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>GitHub-App-Prüfung: liest die Ereignisliste der App und meldet nur ein
/// NACHGEWIESENES Fehlen — jeder Fehlerpfad bleibt still (Unknown).</summary>
public class GitHubAppCommentEventProbeTests
{
    private static GitHubAppCommentEventProbe Probe(StubHttpMessageHandler stub)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var jwt = new GitHubAppJwt("12345", rsa.ExportRSAPrivateKeyPem());
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.github.com/") };
        return new GitHubAppCommentEventProbe(http, jwt, NullLogger<GitHubAppCommentEventProbe>.Instance);
    }

    private static StubHttpMessageHandler App(HttpStatusCode code, string body)
        => new(_ => new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

    [Fact]
    public async Task CheckAsync_eventSubscribed_isOk()
    {
        var probe = Probe(App(HttpStatusCode.OK,
            """{"slug":"naudit","events":["pull_request","pull_request_review_comment"]}"""));

        var status = await probe.CheckAsync();

        Assert.Equal(CommentEventState.Ok, status.State);
        Assert.Empty(status.Details);
    }

    [Fact]
    public async Task CheckAsync_eventMissing_isMissing_withDeepLinkAndInstruction()
    {
        var probe = Probe(App(HttpStatusCode.OK, """{"slug":"naudit","events":["pull_request"]}"""));

        var status = await probe.CheckAsync();

        Assert.Equal(CommentEventState.Missing, status.State);
        var detail = Assert.Single(status.Details);
        // Die Meldung MUSS handlungsleitend sein: Link auf die App-Settings + der Ereignisname.
        Assert.Contains("https://github.com/settings/apps/naudit/permissions", detail);
        Assert.Contains("pull_request_review_comment", detail);
    }

    [Fact]
    public async Task CheckAsync_eventMissingAndNoSlug_stillMissing_withoutBrokenLink()
    {
        var probe = Probe(App(HttpStatusCode.OK, """{"events":[]}"""));

        var status = await probe.CheckAsync();

        Assert.Equal(CommentEventState.Missing, status.State);
        // Ohne Slug darf kein halbfertiger Link entstehen ("…/apps//permissions").
        Assert.DoesNotContain("/apps//", Assert.Single(status.Details));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    public async Task CheckAsync_httpError_isUnknown_notMissing(HttpStatusCode code, string body)
    {
        var probe = Probe(App(code, body));

        // Kein Fehlalarm: eine kaputte API sagt NICHTS über das Abonnement aus.
        Assert.Equal(CommentEventState.Unknown, (await probe.CheckAsync()).State);
    }

    [Fact]
    public async Task CheckAsync_responseWithoutEventsField_isUnknown()
    {
        var probe = Probe(App(HttpStatusCode.OK, """{"slug":"naudit"}"""));

        Assert.Equal(CommentEventState.Unknown, (await probe.CheckAsync()).State);
    }

    [Fact]
    public async Task CheckAsync_transportFailure_isUnknown_andDoesNotThrow()
    {
        var stub = new StubHttpMessageHandler(_ => throw new HttpRequestException("boom"));

        Assert.Equal(CommentEventState.Unknown, (await Probe(stub).CheckAsync()).State);
    }
}
