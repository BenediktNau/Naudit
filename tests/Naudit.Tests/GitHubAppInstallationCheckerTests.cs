using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitHubAppInstallationCheckerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static GitHubAppInstallationChecker Checker(StubHttpMessageHandler stub, TimeProvider time)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var jwt = new GitHubAppJwt("12345", rsa.ExportRSAPrivateKeyPem(), time);
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.github.com/") };
        return new GitHubAppInstallationChecker(http, jwt, NullLogger<GitHubAppInstallationChecker>.Instance, time);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Stub: GET /app → slug; GET /users/{login}/installation und /orgs/{login}/installation je nach Login.
    private static StubHttpMessageHandler Api(Func<string, HttpResponseMessage> installation)
        => new(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/app") return Json(HttpStatusCode.OK, """{"slug":"naudit"}""");
            return installation(path);
        });

    [Fact]
    public async Task GetStatusAsync_userInstalled_returnsInstalledTrue_andDeepLink()
    {
        var stub = Api(path => path.StartsWith("/users/") ? Json(HttpStatusCode.OK, """{"id":1}""") : new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = Checker(stub, new FakeTime(T0));

        var status = await checker.GetStatusAsync(["octocat"]);

        Assert.Equal("https://github.com/apps/naudit/installations/new", status.InstallUrl);
        var acct = Assert.Single(status.Accounts);
        Assert.Equal("octocat", acct.Login);
        Assert.True(acct.Installed);
    }

    [Fact]
    public async Task GetStatusAsync_notUserButOrgInstalled_fallsBackToOrg_returnsTrue()
    {
        var stub = Api(path =>
            path.StartsWith("/users/") ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : path.StartsWith("/orgs/") ? Json(HttpStatusCode.OK, """{"id":2}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = Checker(stub, new FakeTime(T0));

        var status = await checker.GetStatusAsync(["my-org"]);

        Assert.True(Assert.Single(status.Accounts).Installed);
        Assert.Contains(stub.Calls, c => c.Uri!.AbsolutePath == "/orgs/my-org/installation");
    }

    [Fact]
    public async Task GetStatusAsync_notInstalledAnywhere_returnsFalse()
    {
        var stub = Api(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = Checker(stub, new FakeTime(T0));

        Assert.False(Assert.Single((await checker.GetStatusAsync(["nobody"])).Accounts).Installed);
    }

    [Fact]
    public async Task GetStatusAsync_apiError_returnsNull_failQuiet()
    {
        var stub = Api(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var checker = Checker(stub, new FakeTime(T0));

        Assert.Null(Assert.Single((await checker.GetStatusAsync(["octocat"])).Accounts).Installed);
    }

    [Fact]
    public async Task GetStatusAsync_cachesResultAndSlug_withinTtl()
    {
        var stub = Api(path => path.StartsWith("/users/") ? Json(HttpStatusCode.OK, """{"id":1}""") : new HttpResponseMessage(HttpStatusCode.NotFound));
        var time = new FakeTime(T0);
        var checker = Checker(stub, time);

        await checker.GetStatusAsync(["octocat"]);
        var callsAfterFirst = stub.Calls.Count;  // /app + /users/octocat/installation = 2
        await checker.GetStatusAsync(["octocat"]);

        // 2. Aufruf innerhalb der TTL: kein weiterer HTTP-Call (Slug + Ergebnis gecached).
        Assert.Equal(2, callsAfterFirst);
        Assert.Equal(2, stub.Calls.Count);
    }

    [Fact]
    public async Task GetStatusAsync_errorNotCached_retriedOnNextCall()
    {
        var fail = true;
        var stub = new StubHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/app") return Json(HttpStatusCode.OK, """{"slug":"naudit"}""");
            if (fail) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return path.StartsWith("/users/") ? Json(HttpStatusCode.OK, """{"id":1}""") : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var checker = Checker(stub, new FakeTime(T0));

        Assert.Null(Assert.Single((await checker.GetStatusAsync(["octocat"])).Accounts).Installed);
        fail = false;
        // Fehler wurde NICHT gecached ⇒ erneuter Probe-Call, jetzt installiert.
        Assert.True(Assert.Single((await checker.GetStatusAsync(["octocat"])).Accounts).Installed);
    }

    [Fact]
    public async Task GetStatusAsync_escapesLoginInPath_noThrow()
    {
        // Login mit Sonderzeichen (Admin-Freitext): muss escaped rausgehen und darf nicht werfen.
        var stub = Api(_ => new HttpResponseMessage(HttpStatusCode.NotFound)); // user + org 404 ⇒ false
        var checker = Checker(stub, new FakeTime(T0));

        var status = await checker.GetStatusAsync(["a b"]);

        Assert.False(Assert.Single(status.Accounts).Installed);
        Assert.Contains(stub.Calls, c => c.Uri!.AbsolutePath == "/users/a%20b/installation");
    }

    [Fact]
    public async Task GetStatusAsync_malformedSlugResponse_failsQuiet_emptyInstallUrl()
    {
        // /app liefert 200, aber keinen JSON-Body ⇒ JsonException. Fail-quiet: nicht werfen,
        // installUrl leer, Login-Status trotzdem ermittelt.
        var stub = new StubHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/app") return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<not json>", System.Text.Encoding.UTF8, "application/json"),
            };
            return path.StartsWith("/users/") ? Json(HttpStatusCode.OK, """{"id":1}""") : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var checker = Checker(stub, new FakeTime(T0));

        var status = await checker.GetStatusAsync(["octocat"]);

        Assert.Equal("", status.InstallUrl);
        Assert.True(Assert.Single(status.Accounts).Installed);
    }
}
