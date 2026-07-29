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
        Assert.Contains("GitLab-Projekt 42 ", detail);
        Assert.Contains("Comments", detail);
    }

    [Fact]
    public async Task CheckAsync_matchingHookWithoutNoteEventsField_isUnknown_notMissing()
    {
        // Finding 1: ein passender Hook, dessen JSON note_events schlicht nicht enthält, darf
        // NICHT als "false" (und damit Missing) gewertet werden — das wäre ein Fehlalarm.
        using var db = NewDb();
        SeedProjects(db, "42");
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            $$"""[{"url":"{{HookUrl}}"}]"""));

        var status = await Probe(db, stub).CheckAsync();

        Assert.Equal(CommentEventState.Unknown, status.State);
        Assert.Empty(status.Details);
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
    public async Task CheckAsync_twoMatchingHooks_anyWithNoteEvents_isOk()
    {
        // Cheap fix: FirstOrDefault -> "irgendein passender Hook hat note_events". Ein veralteter
        // Hook ohne das Feld steht hier VOR dem korrekten, damit ein FirstOrDefault-Rest bestehen bliebe.
        using var db = NewDb();
        SeedProjects(db, "42");
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            $$"""[{"url":"{{HookUrl}}"},{"url":"{{HookUrl}}","note_events":true}]"""));

        Assert.Equal(CommentEventState.Ok, (await Probe(db, stub).CheckAsync()).State);
    }

    [Fact]
    public async Task CheckAsync_hooksPagesWithPerPage100()
    {
        using var db = NewDb();
        SeedProjects(db, "42");
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            $$"""[{"url":"{{HookUrl}}","note_events":true}]"""));

        await Probe(db, stub).CheckAsync();

        var req = Assert.Single(stub.Requests);
        Assert.Equal("per_page=100", req.RequestUri!.Query.TrimStart('?'));
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
        // Volles Fragment statt bloß "2" pinnen — sonst passt der Assert auch auf "22" o. Ä.
        Assert.Contains("GitLab-Projekt 2 ", Assert.Single(status.Details));
    }

    [Fact]
    public async Task CheckAsync_capsAtMaxProjects_newestFirst()
    {
        using var db = NewDb();
        // Einfügung und LastReviewedAt entkoppeln: erst Reverse-Reihenfolge einfügen,
        // dann LastReviewedAt unabhängig vom Index setzen. So testet man, dass die
        // Implementierung wirklich nach LastReviewedAt sortiert und nicht nach Id/Einfügung.
        var t = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 25; i >= 1; i--)
        {
            db.Projects.Add(new ProjectEntity
            {
                PlatformProjectId = i.ToString(),
                FirstReviewedAt = t,
                LastReviewedAt = t.AddMinutes(i),  // Projekt 1 = älteste (t+1), Projekt 25 = jüngste (t+25)
            });
        }
        db.SaveChanges();

        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            $$"""[{"url":"{{HookUrl}}","note_events":true}]"""));

        await Probe(db, stub).CheckAsync();

        Assert.Equal(GitLabCommentEventProbe.MaxProjects, stub.Calls.Count);
        // Prüfung: die 20 jüngsten Projekte (25-6) müssen abgefragt sein.
        // Projekt 6 ist das älteste der 20 gewählten (t+6), Projekt 5 (t+5) fällt gerade außerhalb.
        Assert.Contains(stub.Calls, c => c.Uri!.AbsolutePath.Contains("/projects/25/"));
        Assert.Contains(stub.Calls, c => c.Uri!.AbsolutePath.Contains("/projects/6/"));
        Assert.DoesNotContain(stub.Calls, c => c.Uri!.AbsolutePath.Contains("/projects/5/"));
        Assert.DoesNotContain(stub.Calls, c => c.Uri!.AbsolutePath.Contains("/projects/1/hooks"));
    }

    [Fact]
    public async Task CheckAsync_ok_summaryNamesCheckedAndUnknownCounts()
    {
        using var db = NewDb();
        SeedProjects(db, "1", "2");
        var stub = new StubHttpMessageHandler(req => Json(
            req.RequestUri!.AbsolutePath.Contains("/projects/2/") ? HttpStatusCode.Forbidden : HttpStatusCode.OK,
            $$"""[{"url":"{{HookUrl}}","note_events":true}]"""));

        var status = await Probe(db, stub).CheckAsync();

        Assert.Equal(CommentEventState.Ok, status.State);
        Assert.Contains("1 Projekte geprüft", status.Summary);
        Assert.Contains("1 nicht ermittelbar", status.Summary);
    }

    [Fact]
    public async Task CheckAsync_emptyPublicBaseUrl_summaryNamesReason()
    {
        using var db = NewDb();
        SeedProjects(db, "42");
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "[]"));

        var status = await Probe(db, stub, publicBaseUrl: "").CheckAsync();

        Assert.Contains("PublicBaseUrl", status.Summary);
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
