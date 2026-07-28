using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Setup;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>GitLab-Prüfung: je Projekt den Naudit-Hook suchen und note_events lesen.
/// Kein passender Hook ⇒ KEINE Aussage (Gruppen-Hooks tauchen in der Projektliste nie auf).</summary>
public class GitLabCommentEventProbeTests
{
    private const string BaseUrl = "https://naudit.example.com";
    private const string HookUrl = BaseUrl + "/webhook/gitlab";

    /// <summary>Temp-File-SQLite + Migrate — gleiches Muster wie DbReviewMemoryTests.</summary>
    private static NauditDbContext NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"naudit-eventprobe-{Guid.NewGuid():N}.db");
        var db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        db.Database.Migrate();
        return db;
    }

    private static void SeedProjects(NauditDbContext db, params string[] platformIds)
    {
        var t = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var id in platformIds)
        {
            db.Projects.Add(new ProjectEntity
            {
                PlatformProjectId = id,
                FirstReviewedAt = t,
                // Aufsteigend, damit die zuletzt hinzugefügten die jüngsten sind.
                LastReviewedAt = t.AddMinutes(Array.IndexOf(platformIds, id)),
            });
        }
        db.SaveChanges();
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static GitLabCommentEventProbe Probe(
        NauditDbContext db, StubHttpMessageHandler stub, string publicBaseUrl = BaseUrl)
        => new(new HttpClient(stub) { BaseAddress = new Uri("https://gitlab.example.com/") },
            new ConfiguredGitTokenProvider("global-token", []),
            db, publicBaseUrl, NullLogger<GitLabCommentEventProbe>.Instance);

    [Fact]
    public async Task CheckAsync_hookWithNoteEvents_isOk()
    {
        using var db = NewDb();
        SeedProjects(db, "42");
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            $$"""[{"url":"{{HookUrl}}","note_events":true}]"""));

        Assert.Equal(CommentEventState.Ok, (await Probe(db, stub).CheckAsync()).State);
    }

    [Fact]
    public async Task CheckAsync_hookWithoutNoteEvents_isMissing_withProjectIdAndInstruction()
    {
        using var db = NewDb();
        SeedProjects(db, "42");
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            $$"""[{"url":"{{HookUrl}}","note_events":false}]"""));

        var status = await Probe(db, stub).CheckAsync();

        Assert.Equal(CommentEventState.Missing, status.State);
        var detail = Assert.Single(status.Details);
        Assert.Contains("42", detail);
        Assert.Contains("Comments", detail);
    }

    [Fact]
    public async Task CheckAsync_noMatchingHook_isUnknown_notMissing()
    {
        using var db = NewDb();
        SeedProjects(db, "42");
        // Fremder Hook: der Naudit-Hook könnte auf Gruppenebene hängen und taucht hier nie auf.
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """[{"url":"https://other.example.com/hook","note_events":false}]"""));

        Assert.Equal(CommentEventState.Unknown, (await Probe(db, stub).CheckAsync()).State);
    }

    [Fact]
    public async Task CheckAsync_httpError_isUnknown()
    {
        using var db = NewDb();
        SeedProjects(db, "42");
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        Assert.Equal(CommentEventState.Unknown, (await Probe(db, stub).CheckAsync()).State);
    }

    [Fact]
    public async Task CheckAsync_emptyPublicBaseUrl_isUnknown_withoutAnyHttpCall()
    {
        using var db = NewDb();
        SeedProjects(db, "42");
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "[]"));

        var status = await Probe(db, stub, publicBaseUrl: "").CheckAsync();

        Assert.Equal(CommentEventState.Unknown, status.State);
        // Ohne Vergleichsmaßstab gibt es nichts zu prüfen — dann auch keine API-Last erzeugen.
        Assert.Empty(stub.Calls);
    }

    [Fact]
    public async Task CheckAsync_noProjects_isUnknown()
    {
        using var db = NewDb();
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "[]"));

        Assert.Equal(CommentEventState.Unknown, (await Probe(db, stub).CheckAsync()).State);
    }

    [Fact]
    public async Task CheckAsync_oneBrokenAmongHealthy_reportsMissing()
    {
        using var db = NewDb();
        SeedProjects(db, "1", "2");
        var stub = new StubHttpMessageHandler(req => Json(HttpStatusCode.OK,
            req.RequestUri!.AbsolutePath.Contains("/projects/2/")
                ? $$"""[{"url":"{{HookUrl}}","note_events":false}]"""
                : $$"""[{"url":"{{HookUrl}}","note_events":true}]"""));

        var status = await Probe(db, stub).CheckAsync();

        Assert.Equal(CommentEventState.Missing, status.State);
        Assert.Contains("2", Assert.Single(status.Details));
    }

    [Fact]
    public async Task CheckAsync_capsAtMaxProjects_newestFirst()
    {
        using var db = NewDb();
        SeedProjects(db, Enumerable.Range(1, 25).Select(i => i.ToString()).ToArray());
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            $$"""[{"url":"{{HookUrl}}","note_events":true}]"""));

        await Probe(db, stub).CheckAsync();

        Assert.Equal(GitLabCommentEventProbe.MaxProjects, stub.Calls.Count);
        // Jüngstes zuerst: Projekt 25 wurde zuletzt reviewt, Projekt 5 fällt gerade noch rein,
        // Projekt 1 (ältestes) darf gar nicht abgefragt werden.
        Assert.Contains(stub.Calls, c => c.Uri!.AbsolutePath.Contains("/projects/25/"));
        Assert.DoesNotContain(stub.Calls, c => c.Uri!.AbsolutePath.Contains("/projects/1/hooks"));
    }

    [Fact]
    public async Task CheckAsync_sendsPrivateTokenHeader()
    {
        using var db = NewDb();
        SeedProjects(db, "42");
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            $$"""[{"url":"{{HookUrl}}","note_events":true}]"""));

        await Probe(db, stub).CheckAsync();

        var req = Assert.Single(stub.Requests);
        Assert.Equal("global-token", Assert.Single(req.Headers.GetValues("PRIVATE-TOKEN")));
    }
}
