# Kommentar-Event-Prüfung Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Naudit prüft einmal nach dem Start, ob die Plattform ihm Antworten auf Inline-Kommentare überhaupt zustellt, und schreibt bei Fehlanzeige eine Warnung mit Handlungsanweisung ins Log.

**Architecture:** Ein Seam `ICommentEventProbe` mit je einer Implementierung pro Plattform (GitHub-App: `GET /app` → `events[]`; GitLab: `GET /projects/{id}/hooks` → `note_events`). Ein `BackgroundService` löst den Probe einmal aus einem eigenen Scope auf und loggt nur den Zustand `Missing`. Registriert wird nur der Probe der aktiven Plattform; ohne Registrierung tut der Dienst nichts.

**Tech Stack:** .NET 10, ASP.NET, `System.Text.Json`, EF Core, xUnit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-28-comment-event-check-design.md`.
- Solution-Datei ist `Naudit.slnx` — `dotnet test Naudit.sln` schlägt mit MSB1009 fehl.
- **Jeden dotnet-Befehl mit `DOTNET_USE_POLLING_FILE_WATCHER=1` präfixen.** Ohne die Variable scheitern auf dieser Maschine 81 `WebApplicationFactory`-Tests am inotify-Limit — Umgebung, nicht Code.
- Code-Kommentare auf Deutsch (Projektkonvention).
- **Die Core-Regel bleibt unangetastet:** `Naudit.Core` bekommt nichts. Alles liegt in `Naudit.Infrastructure/Setup/`.
- **Fail-quiet ohne Fehlalarm:** jeder Fehlerpfad (HTTP-Fehler, fehlende Rechte, unerwartetes JSON, fehlender `PublicBaseUrl`, kein passender Hook) endet in `Unknown` bzw. einem übersprungenen Projekt — **nie** in einer Warnung und nie in einer Ausnahme nach außen. Nur ein nachgewiesenes Fehlen des Events warnt.
- **Nur prüfen, nie schreiben.** Kein `PUT`, kein `POST` gegen die Plattform.
- TDD: erst der fehlschlagende Test, dann die Implementierung, ein Commit pro Task.

## File Structure

| Datei | Verantwortung |
| --- | --- |
| `src/Naudit.Infrastructure/Setup/ICommentEventProbe.cs` (neu) | Der Seam: Interface, `CommentEventState`, `CommentEventStatus` |
| `src/Naudit.Infrastructure/Setup/GitHubAppCommentEventProbe.cs` (neu) | GitHub-App-Prüfung über `GET /app` |
| `src/Naudit.Infrastructure/Setup/GitLabCommentEventProbe.cs` (neu) | GitLab-Prüfung je Projekt über `GET /projects/{id}/hooks` |
| `src/Naudit.Infrastructure/Setup/CommentEventCheckService.cs` (neu) | `BackgroundService`, führt einmal aus und loggt `Missing` |
| `src/Naudit.Infrastructure/DependencyInjection.cs` (ändern) | Registrierung je Plattformzweig + Hosted Service |
| `tests/Naudit.Tests/GitHubAppCommentEventProbeTests.cs` (neu) | |
| `tests/Naudit.Tests/GitLabCommentEventProbeTests.cs` (neu) | |
| `tests/Naudit.Tests/CommentEventCheckServiceTests.cs` (neu) | |
| `docs/review-memory.md` (ändern) | Abschnitt zur Startup-Prüfung beim Antwort-Kommando |

---

### Task 1: Seam + GitHub-App-Probe

**Files:**
- Create: `src/Naudit.Infrastructure/Setup/ICommentEventProbe.cs`
- Create: `src/Naudit.Infrastructure/Setup/GitHubAppCommentEventProbe.cs`
- Test: `tests/Naudit.Tests/GitHubAppCommentEventProbeTests.cs`

**Interfaces:**
- Consumes: `Naudit.Infrastructure.Git.GitHub.GitHubAppJwt` mit `string Create()` — erzeugt das App-JWT.
- Produces:
  - `public interface ICommentEventProbe { Task<CommentEventStatus> CheckAsync(CancellationToken ct = default); }`
  - `public enum CommentEventState { Ok, Missing, Unknown }`
  - `public sealed record CommentEventStatus(CommentEventState State, IReadOnlyList<string> Details)` mit den statischen Kürzeln `CommentEventStatus.Ok` und `CommentEventStatus.Unknown`
  - `public sealed class GitHubAppCommentEventProbe(HttpClient http, GitHubAppJwt jwt, ILogger<GitHubAppCommentEventProbe> logger) : ICommentEventProbe` mit `public const string RequiredEvent = "pull_request_review_comment"`

  Task 2 implementiert dasselbe Interface, Task 3 konsumiert es.

- [ ] **Step 1: Write the failing tests**

Neue Datei `tests/Naudit.Tests/GitHubAppCommentEventProbeTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~GitHubAppCommentEventProbeTests"`
Expected: Compile-Fehler — weder `ICommentEventProbe` noch `GitHubAppCommentEventProbe` existieren.

- [ ] **Step 3: Create the seam**

Neue Datei `src/Naudit.Infrastructure/Setup/ICommentEventProbe.cs`:

```csharp
namespace Naudit.Infrastructure.Setup;

/// <summary>Prüft, ob die Plattform-Seite Naudit die Antworten auf Inline-Kommentare überhaupt
/// zustellt — GitHub die Ereignisart "pull_request_review_comment", GitLab note_events. Fehlt das
/// Abonnement, fallen die "@naudit fp"/"@naudit ok"-Kommandos still aus: kein Fehler, keine
/// Log-Zeile, keine Antwort im Thread.</summary>
public interface ICommentEventProbe
{
    Task<CommentEventStatus> CheckAsync(CancellationToken ct = default);
}

/// <summary>Unknown = nicht ermittelbar (API-Fehler, fehlende Rechte, Gruppen-Hook). Erzeugt
/// bewusst KEINE Warnung: wer sich an Fehlalarme gewöhnt, übersieht den echten Fall.</summary>
public enum CommentEventState { Ok, Missing, Unknown }

/// <summary>Details sind fertige Handlungsanweisungen fürs Log — je betroffenem Ziel eine.</summary>
public sealed record CommentEventStatus(CommentEventState State, IReadOnlyList<string> Details)
{
    public static readonly CommentEventStatus Ok = new(CommentEventState.Ok, []);
    public static readonly CommentEventStatus Unknown = new(CommentEventState.Unknown, []);
}
```

- [ ] **Step 4: Implement the GitHub probe**

Neue Datei `src/Naudit.Infrastructure/Setup/GitHubAppCommentEventProbe.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Git.GitHub;

namespace Naudit.Infrastructure.Setup;

/// <summary>GET /app liefert die Ereignisliste der eigenen App. Nur bei Auth=App registriert —
/// im PAT-Modus gibt es keine App, deren Liste man abfragen könnte.</summary>
public sealed class GitHubAppCommentEventProbe(
    HttpClient http, GitHubAppJwt jwt, ILogger<GitHubAppCommentEventProbe> logger) : ICommentEventProbe
{
    public const string RequiredEvent = "pull_request_review_comment";

    public async Task<CommentEventStatus> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "app");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt.Create());
            using var res = await http.SendAsync(req, ct);
            if (res.StatusCode != HttpStatusCode.OK)
            {
                logger.LogDebug("Kommentar-Event-Prüfung: GET /app lieferte {Status}.", (int)res.StatusCode);
                return CommentEventStatus.Unknown;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("events", out var events)
                || events.ValueKind != JsonValueKind.Array)
                return CommentEventStatus.Unknown;

            foreach (var e in events.EnumerateArray())
                if (string.Equals(e.GetString(), RequiredEvent, StringComparison.Ordinal))
                    return CommentEventStatus.Ok;

            // Ohne Slug keinen halbfertigen Link bauen — dann lieber im Klartext hinweisen.
            var slug = doc.RootElement.TryGetProperty("slug", out var s) ? s.GetString() : null;
            var where = string.IsNullOrEmpty(slug)
                ? "den Einstellungen der GitHub-App"
                : $"https://github.com/settings/apps/{slug}/permissions";

            return new CommentEventStatus(CommentEventState.Missing, [
                $"Antwort-Kommandos sind wirkungslos — die GitHub-App ist nicht auf '{RequiredEvent}' " +
                "abonniert. @naudit fp / @naudit ok werden nie zugestellt. Beheben: " +
                $"{where} → \"Subscribe to events\" → \"Pull request review comment\" anhaken → Save. " +
                "Wirkt sofort für bestehende Installationen; es ändert sich keine Permission, also " +
                "sind weder Neuinstallation noch Bestätigung durch die Nutzer nötig."]);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Kommentar-Event-Prüfung der GitHub-App fehlgeschlagen.");
            return CommentEventStatus.Unknown;
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~GitHubAppCommentEventProbeTests"`
Expected: PASS (7 Tests — fünf `[Fact]` plus zwei `[Theory]`-Fälle).

- [ ] **Step 6: Run the full suite**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS, keine Regression.

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Infrastructure/Setup/ICommentEventProbe.cs src/Naudit.Infrastructure/Setup/GitHubAppCommentEventProbe.cs tests/Naudit.Tests/GitHubAppCommentEventProbeTests.cs
git commit -m "feat(setup): Pruefung der GitHub-App auf das Kommentar-Event"
```

---

### Task 2: GitLab-Probe

**Files:**
- Create: `src/Naudit.Infrastructure/Setup/GitLabCommentEventProbe.cs`
- Test: `tests/Naudit.Tests/GitLabCommentEventProbeTests.cs`

**Interfaces:**
- Consumes: `ICommentEventProbe`, `CommentEventState`, `CommentEventStatus` (Task 1); `Naudit.Infrastructure.Git.IGitTokenProvider` mit `ValueTask<string> ResolveTokenAsync(string projectId, CancellationToken ct = default)`; `Naudit.Infrastructure.Data.NauditDbContext` mit `DbSet<ProjectEntity> Projects`, wobei `ProjectEntity` die Felder `PlatformProjectId` (string) und `LastReviewedAt` (DateTime) hat.
- Produces: `public sealed class GitLabCommentEventProbe(HttpClient http, IGitTokenProvider tokens, NauditDbContext db, string publicBaseUrl, ILogger<GitLabCommentEventProbe> logger) : ICommentEventProbe` mit `public const int MaxProjects = 20`.

- [ ] **Step 1: Write the failing tests**

Neue Datei `tests/Naudit.Tests/GitLabCommentEventProbeTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~GitLabCommentEventProbeTests"`
Expected: Compile-Fehler — `GitLabCommentEventProbe` existiert nicht.

- [ ] **Step 3: Implement the GitLab probe**

Neue Datei `src/Naudit.Infrastructure/Setup/GitLabCommentEventProbe.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;

namespace Naudit.Infrastructure.Setup;

/// <summary>Prüft je Projekt den Naudit-Webhook auf note_events. Projektauswahl sind die
/// ProjectEntity-Zeilen (Projekte mit mindestens einem Review), jüngste zuerst und gedeckelt —
/// eine frische Installation hat keine und wird still übersprungen.</summary>
public sealed class GitLabCommentEventProbe(
    HttpClient http, IGitTokenProvider tokens, NauditDbContext db,
    string publicBaseUrl, ILogger<GitLabCommentEventProbe> logger) : ICommentEventProbe
{
    public const int MaxProjects = 20;

    private sealed record HookDto(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("note_events")] bool NoteEvents);

    public async Task<CommentEventStatus> CheckAsync(CancellationToken ct = default)
    {
        // Ohne bekannte öffentliche URL fehlt der Vergleichsmaßstab: welcher der Hooks Naudits
        // ist, wäre geraten. Dann lieber keine Aussage — und keine API-Last.
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
            return CommentEventStatus.Unknown;
        var webhookUrl = $"{publicBaseUrl.TrimEnd('/')}/webhook/gitlab";

        List<string> projects;
        try
        {
            projects = await db.Projects
                .OrderByDescending(p => p.LastReviewedAt)
                .Take(MaxProjects)
                .Select(p => p.PlatformProjectId)
                .ToListAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Kommentar-Event-Prüfung: Projektliste nicht lesbar.");
            return CommentEventStatus.Unknown;
        }

        var details = new List<string>();
        var anyChecked = false;
        foreach (var projectId in projects)
        {
            var noteEvents = await ProbeProjectAsync(projectId, webhookUrl, ct);
            if (noteEvents is null)
                continue;   // nicht ermittelbar oder kein Projekt-Hook — keine Aussage
            anyChecked = true;
            if (!noteEvents.Value)
                details.Add(
                    $"Antwort-Kommandos sind für GitLab-Projekt {projectId} wirkungslos — der " +
                    "Naudit-Webhook hat den Trigger \"Comments\" (note_events) nicht. @naudit fp / " +
                    "@naudit ok werden nie zugestellt. Beheben: Projekt → Settings → Webhooks → den " +
                    "Naudit-Hook bearbeiten → \"Comments\" anhaken → Save.");
        }

        if (details.Count > 0)
            return new CommentEventStatus(CommentEventState.Missing, details);
        return anyChecked ? CommentEventStatus.Ok : CommentEventStatus.Unknown;
    }

    /// <summary>true/false = note_events des Naudit-Hooks; null = keine Aussage möglich.</summary>
    private async Task<bool?> ProbeProjectAsync(string projectId, string webhookUrl, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"api/v4/projects/{Uri.EscapeDataString(projectId)}/hooks");
            req.Headers.Add("PRIVATE-TOKEN", await tokens.ResolveTokenAsync(projectId, ct));
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogDebug("Kommentar-Event-Prüfung: /hooks für Projekt {Project} lieferte {Status}.",
                    projectId, (int)res.StatusCode);
                return null;
            }

            var hooks = JsonSerializer.Deserialize<List<HookDto>>(await res.Content.ReadAsStringAsync(ct));
            var hook = hooks?.FirstOrDefault(h =>
                string.Equals(h.Url?.TrimEnd('/'), webhookUrl, StringComparison.OrdinalIgnoreCase));
            // Kein passender Projekt-Hook ⇒ null, NICHT false: ein Gruppen-Hook wirkt auf das
            // Projekt, taucht in dieser Liste aber nie auf. Eine Warnung wäre dort dauerhaft falsch.
            return hook?.NoteEvents;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Kommentar-Event-Prüfung für GitLab-Projekt {Project} fehlgeschlagen.", projectId);
            return null;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~GitLabCommentEventProbeTests"`
Expected: PASS (9 Tests).

- [ ] **Step 5: Run the full suite**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Setup/GitLabCommentEventProbe.cs tests/Naudit.Tests/GitLabCommentEventProbeTests.cs
git commit -m "feat(setup): Pruefung des GitLab-Webhooks auf note_events"
```

---

### Task 3: Hintergrunddienst, Verdrahtung und Doku

**Files:**
- Create: `src/Naudit.Infrastructure/Setup/CommentEventCheckService.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (GitHub-App-Zweig, GitLab-Zweig, und einmal nach dem `switch`)
- Modify: `docs/review-memory.md`
- Test: `tests/Naudit.Tests/CommentEventCheckServiceTests.cs`

**Interfaces:**
- Consumes: `ICommentEventProbe`, `CommentEventState`, `CommentEventStatus` (Task 1); `GitHubAppCommentEventProbe` (Task 1); `GitLabCommentEventProbe` (Task 2).
- Produces: `public sealed class CommentEventCheckService(IServiceScopeFactory scopes, ILogger<CommentEventCheckService> logger) : BackgroundService`.

- [ ] **Step 1: Write the failing tests**

Neue Datei `tests/Naudit.Tests/CommentEventCheckServiceTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Setup;
using Xunit;

namespace Naudit.Tests;

/// <summary>Der Dienst warnt ausschließlich bei nachgewiesenem Missing und überlebt einen
/// werfenden Probe — eine Diagnose darf den Host nie kippen.</summary>
public class CommentEventCheckServiceTests
{
    private sealed class RecordingLogger : ILogger<CommentEventCheckService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FakeProbe(Func<CommentEventStatus> result) : ICommentEventProbe
    {
        public Task<CommentEventStatus> CheckAsync(CancellationToken ct = default)
            => Task.FromResult(result());
    }

    private static async Task<RecordingLogger> RunAsync(ICommentEventProbe? probe)
    {
        var services = new ServiceCollection();
        if (probe is not null) services.AddScoped<ICommentEventProbe>(_ => probe);
        var logger = new RecordingLogger();
        var service = new CommentEventCheckService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), logger);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        return logger;
    }

    [Fact]
    public async Task Missing_logsOneWarningPerDetail()
    {
        var logger = await RunAsync(new FakeProbe(() =>
            new CommentEventStatus(CommentEventState.Missing, ["erste Anweisung", "zweite Anweisung"])));

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("erste Anweisung"));
        Assert.Contains(warnings, w => w.Message.Contains("zweite Anweisung"));
    }

    [Fact]
    public async Task Ok_logsNoWarning()
    {
        var logger = await RunAsync(new FakeProbe(() => CommentEventStatus.Ok));

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Unknown_logsNoWarning()
    {
        // Kein Fehlalarm: "nicht ermittelbar" ist kein Befund.
        var logger = await RunAsync(new FakeProbe(() => CommentEventStatus.Unknown));

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task NoProbeRegistered_doesNothing()
    {
        // GitHub im PAT-Modus: kein Probe im Container, der Dienst muss trotzdem sauber laufen.
        var logger = await RunAsync(null);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task ThrowingProbe_doesNotPropagate()
    {
        var logger = await RunAsync(new FakeProbe(() => throw new InvalidOperationException("boom")));

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~CommentEventCheckServiceTests"`
Expected: Compile-Fehler — `CommentEventCheckService` existiert nicht.

- [ ] **Step 3: Implement the service**

Neue Datei `src/Naudit.Infrastructure/Setup/CommentEventCheckService.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Naudit.Infrastructure.Setup;

/// <summary>Führt die Kommentar-Event-Prüfung EINMAL nach dem Hochfahren aus. Bewusst
/// BackgroundService statt IHostedService.StartAsync: die Prüfung macht einen HTTP-Aufruf, und ein
/// hängender Aufruf darf den Hoststart nicht blockieren. Ist kein Probe registriert (GitHub im
/// PAT-Modus), passiert nichts.</summary>
public sealed class CommentEventCheckService(
    IServiceScopeFactory scopes, ILogger<CommentEventCheckService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var probe = scope.ServiceProvider.GetService<ICommentEventProbe>();
            if (probe is null) return;

            var status = await probe.CheckAsync(ct);
            if (status.State != CommentEventState.Missing) return;

            foreach (var detail in status.Details)
                logger.LogWarning("{Detail}", detail);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Eine Diagnose darf den Host nie kippen (Audit-Sink-Philosophie).
            logger.LogDebug(ex, "Kommentar-Event-Prüfung fehlgeschlagen.");
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~CommentEventCheckServiceTests"`
Expected: PASS (5 Tests).

- [ ] **Step 5: Register the GitHub probe**

In `src/Naudit.Infrastructure/DependencyInjection.cs`, im GitHub-Zweig direkt **nach** der `IGitHubAppInstallationChecker`-Registrierung (sie endet mit `CreateLogger<GitHubAppInstallationChecker>()));`) einfügen:

```csharp
                    // Kommentar-Event-Prüfung: gleicher named Client und geteiltes App-JWT.
                    services.AddScoped<Naudit.Infrastructure.Setup.ICommentEventProbe>(sp =>
                        new Naudit.Infrastructure.Setup.GitHubAppCommentEventProbe(
                            sp.GetRequiredService<IHttpClientFactory>().CreateClient("github-app"),
                            appJwt,
                            sp.GetRequiredService<ILoggerFactory>()
                                .CreateLogger<Naudit.Infrastructure.Setup.GitHubAppCommentEventProbe>()));
```

- [ ] **Step 6: Register the GitLab probe**

Im GitLab-Zweig (`default: // GitPlatformKind.GitLab`) direkt **vor** dem `break;` einfügen:

```csharp
                // Kommentar-Event-Prüfung: eigener named Client auf denselben GitLab-Host.
                services.AddHttpClient("gitlab-hooks", http =>
                    http.BaseAddress = new Uri(gitLabOptions.BaseUrl.TrimEnd('/') + "/"));
                var publicBaseUrl = configuration["Naudit:PublicBaseUrl"] ?? "";
                services.AddScoped<Naudit.Infrastructure.Setup.ICommentEventProbe>(sp =>
                    new Naudit.Infrastructure.Setup.GitLabCommentEventProbe(
                        sp.GetRequiredService<IHttpClientFactory>().CreateClient("gitlab-hooks"),
                        sp.GetRequiredService<IGitTokenProvider>(),
                        sp.GetRequiredService<NauditDbContext>(),
                        publicBaseUrl,
                        sp.GetRequiredService<ILoggerFactory>()
                            .CreateLogger<Naudit.Infrastructure.Setup.GitLabCommentEventProbe>()));
```

Falls `NauditDbContext` in dieser Datei noch nicht per `using` bekannt ist, den vollen Namen `Naudit.Infrastructure.Data.NauditDbContext` verwenden statt einen using-Block zu ergänzen.

- [ ] **Step 7: Register the hosted service**

In derselben Datei **nach** dem schließenden `}` des `switch (gitOptions.Platform)` einfügen — dort, wo schon plattformunabhängige Registrierungen stehen (z. B. `services.AddScoped<…ReviewCommentCommandService>();`):

```csharp
        // Prüft einmal nach dem Start, ob die Plattform Antworten auf Inline-Kommentare zustellt.
        // Unbedingt registriert: ohne passenden ICommentEventProbe (GitHub im PAT-Modus) tut er nichts.
        services.AddHostedService<Naudit.Infrastructure.Setup.CommentEventCheckService>();
```

- [ ] **Step 8: Run the full suite**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS. Die `WebApplicationFactory`-Tests starten jetzt zusätzlich diesen Hosted Service — er darf dort nichts kaputt machen und keine Ausnahme werfen.

- [ ] **Step 9: Document it**

In `docs/review-memory.md`, ans Ende des Abschnitts „Reply command: `@naudit fp` (PR 2b)" anhängen:

```markdown
### Startup check: is the event even subscribed?

The reply command depends on the platform delivering the reply to Naudit at all —
GitHub via the `pull_request_review_comment` event, GitLab via the Note Hook
(`note_events`, the **Comments** trigger in the UI). If that subscription is
missing, the feature fails **silently**: no error, no log line, no confirmation in
the thread.

The setup wizard subscribes to both, but GitHub never adds events to an *existing*
app retroactively, and a hook created by hand from an older revision of the docs
never had the trigger. Naudit therefore checks once, shortly after startup, and
logs a warning naming the exact fix:

```text
warn: Antwort-Kommandos sind wirkungslos — die GitHub-App ist nicht auf
      'pull_request_review_comment' abonniert. […] Beheben:
      https://github.com/settings/apps/<slug>/permissions → "Subscribe to events" →
      "Pull request review comment" anhaken → Save.
```

The check only ever *reports* — it never changes a hook or an app. On GitHub the
event list cannot be changed through the API at all; on GitLab it could, but the
same read-only rule is applied deliberately.

It stays quiet unless the gap is proven. An API error, missing permissions, or a
GitLab **group** hook (which never appears in a project's own hook list) all yield
"cannot tell" rather than a warning — a check that cries wolf gets ignored.

The GitLab side inspects the projects Naudit has already reviewed, newest first,
capped at 20. A fresh install has none, so the warning first appears after the
first review — which is soon enough, since `merge_requests_events` and
`note_events` are independent.
```

- [ ] **Step 10: Commit**

```bash
git add src/Naudit.Infrastructure/Setup/CommentEventCheckService.cs src/Naudit.Infrastructure/DependencyInjection.cs tests/Naudit.Tests/CommentEventCheckServiceTests.cs docs/review-memory.md
git commit -m "feat(setup): Kommentar-Event-Pruefung beim Start verdrahten"
```

---

## Verifikation zum Schluss

- [ ] `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet build Naudit.slnx` — keine neuen Warnungen aus den neuen Dateien (vorbestehend sind nur `NU1903`-NuGet-Advisories)
- [ ] `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx` — vollständige Suite grün
- [ ] Gegenprobe der Fail-quiet-Zusicherung: in der gesamten neuen Codebasis darf `LogWarning` **nur** in `CommentEventCheckService` vorkommen, und dort nur im `Missing`-Zweig — `grep -rn "LogWarning" src/Naudit.Infrastructure/Setup/*CommentEvent*.cs`
