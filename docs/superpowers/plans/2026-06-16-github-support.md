# GitHub Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Naudit kann GitHub Pull Requests reviewen — als zweite, per Config gewählte `IGitPlatform`-Implementierung, ohne Änderung an `Naudit.Core`.

**Architecture:** Neuer Config-Schalter `Naudit:Git:Platform` (`GitLab` | `GitHub`, Default `GitLab`) wählt in `AddNauditInfrastructure` per `switch`, welche `IGitPlatform` + welcher typed `HttpClient` registriert wird. `Program.cs` mappt nur den Webhook-Endpoint der aktiven Plattform. GitHub-Auth über PAT (Header), Webhook-Verifizierung über HMAC-SHA256 (`X-Hub-Signature-256`). Mapping: `repository.full_name → ProjectId`, `pull_request.number → MergeRequestIid`, `title → Title`.

**Tech Stack:** .NET 10, ASP.NET Minimal API, `System.Net.Http.Json`, `System.Security.Cryptography` (HMACSHA256), xUnit. Solution: `Naudit.slnx` (nicht `.sln`). Code-Kommentare auf Deutsch.

**Spec:** `docs/superpowers/specs/2026-06-16-github-support-design.md`

---

### Task 1: GitHub-DTOs + Webhook→ReviewRequest-Mapping

**Files:**
- Create: `src/Naudit.Infrastructure/Git/GitHub/GitHubDtos.cs`
- Create: `src/Naudit.Infrastructure/Git/GitHub/GitHubWebhook.cs`
- Test: `tests/Naudit.Tests/GitHubWebhookTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/Naudit.Tests/GitHubWebhookTests.cs`:
```csharp
using System.Text.Json;
using Naudit.Infrastructure.Git.GitHub;
using Xunit;

namespace Naudit.Tests;

public class GitHubWebhookTests
{
    private const string PullRequestEvent = """
    {
      "action": "opened",
      "repository": { "full_name": "octo/hello-world" },
      "pull_request": { "number": 42, "title": "Add feature X" }
    }
    """;

    [Fact]
    public void ToReviewRequest_mapsPullRequestEvent()
    {
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(PullRequestEvent)!;

        var request = GitHubWebhook.ToReviewRequest("pull_request", payload);

        Assert.NotNull(request);
        Assert.Equal("octo/hello-world", request!.ProjectId);
        Assert.Equal(42, request.MergeRequestIid);
        Assert.Equal("Add feature X", request.Title);
    }

    [Fact]
    public void ToReviewRequest_ignoresNonPullRequestEvents()
    {
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(PullRequestEvent)!;
        Assert.Null(GitHubWebhook.ToReviewRequest("push", payload));
    }

    [Fact]
    public void ToReviewRequest_ignoresNonReviewableActions()
    {
        var json = """{ "action": "closed", "repository": { "full_name": "o/r" }, "pull_request": { "number": 1, "title": "x" } }""";
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(json)!;
        Assert.Null(GitHubWebhook.ToReviewRequest("pull_request", payload));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubWebhookTests`
Expected: Build-Fehler — `GitHubWebhookPayload`/`GitHubWebhook` existieren nicht (CS0246).

- [ ] **Step 3: Write the DTOs**

`src/Naudit.Infrastructure/Git/GitHub/GitHubDtos.cs`:
```csharp
using System.Text.Json.Serialization;

namespace Naudit.Infrastructure.Git.GitHub;

public sealed class GitHubWebhookPayload
{
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("repository")] public GitHubRepository? Repository { get; set; }
    [JsonPropertyName("pull_request")] public GitHubPullRequest? PullRequest { get; set; }
}

public sealed class GitHubRepository
{
    [JsonPropertyName("full_name")] public string? FullName { get; set; }
}

public sealed class GitHubPullRequest
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
}

public sealed class GitHubFile
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("patch")] public string? Patch { get; set; }
}
```

- [ ] **Step 4: Write the mapping (ohne Signaturprüfung — kommt in Task 2)**

`src/Naudit.Infrastructure/Git/GitHub/GitHubWebhook.cs`:
```csharp
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git.GitHub;

public static class GitHubWebhook
{
    // Reviewbare PR-Actions: neu, wieder geöffnet, neue Commits gepusht.
    private static readonly string[] ReviewableActions = ["opened", "reopened", "synchronize"];

    /// <summary>Mappt ein GitHub-pull_request-Event auf einen ReviewRequest, oder null wenn nichts zu reviewen ist.</summary>
    public static ReviewRequest? ToReviewRequest(string? eventType, GitHubWebhookPayload payload)
    {
        if (eventType != "pull_request")
            return null;

        if (payload.PullRequest is null || payload.Repository?.FullName is null)
            return null;

        if (payload.Action is null || !ReviewableActions.Contains(payload.Action))
            return null;

        return new ReviewRequest(payload.Repository.FullName, payload.PullRequest.Number, payload.PullRequest.Title ?? "");
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubWebhookTests`
Expected: PASS (3 Tests).

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Git/GitHub/GitHubDtos.cs src/Naudit.Infrastructure/Git/GitHub/GitHubWebhook.cs tests/Naudit.Tests/GitHubWebhookTests.cs
git commit -m "feat(infra): add GitHub webhook DTOs and pull_request mapping"
```

---

### Task 2: HMAC-SHA256-Signaturprüfung

**Files:**
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubWebhook.cs`
- Test: `tests/Naudit.Tests/GitHubWebhookTests.cs` (Methoden ergänzen)

- [ ] **Step 1: Write the failing test (Methoden in GitHubWebhookTests ergänzen)**

Oben in der Datei die Usings ergänzen:
```csharp
using System.Security.Cryptography;
using System.Text;
```

Innerhalb der Klasse `GitHubWebhookTests` ergänzen:
```csharp
    private static string Sign(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(body));
    }

    [Fact]
    public void IsValidSignature_acceptsCorrectSignature()
    {
        var body = Encoding.UTF8.GetBytes("""{"hello":"world"}""");
        var header = Sign("topsecret", body);

        Assert.True(GitHubWebhook.IsValidSignature(body, "topsecret", header));
    }

    [Fact]
    public void IsValidSignature_rejectsWrongSecret()
    {
        var body = Encoding.UTF8.GetBytes("""{"hello":"world"}""");
        var header = Sign("topsecret", body);

        Assert.False(GitHubWebhook.IsValidSignature(body, "other-secret", header));
    }

    [Fact]
    public void IsValidSignature_rejectsMissingOrMalformedHeader()
    {
        var body = Encoding.UTF8.GetBytes("x");
        Assert.False(GitHubWebhook.IsValidSignature(body, "topsecret", null));
        Assert.False(GitHubWebhook.IsValidSignature(body, "topsecret", "not-a-signature"));
        Assert.False(GitHubWebhook.IsValidSignature(body, "topsecret", "sha256=zzzz"));
    }

    [Fact]
    public void IsValidSignature_rejectsEmptySecret_failClosed()
    {
        var body = Encoding.UTF8.GetBytes("x");
        Assert.False(GitHubWebhook.IsValidSignature(body, "", Sign("", body)));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~IsValidSignature"`
Expected: Build-Fehler — `GitHubWebhook.IsValidSignature` existiert nicht (CS0117).

- [ ] **Step 3: Implement IsValidSignature**

In `src/Naudit.Infrastructure/Git/GitHub/GitHubWebhook.cs` die Usings ergänzen:
```csharp
using System.Security.Cryptography;
using System.Text;
```

Innerhalb der Klasse `GitHubWebhook` ergänzen:
```csharp
    /// <summary>Prüft die GitHub-Webhook-Signatur (HMAC-SHA256 über den rohen Body) konstant-zeitlich. Leeres Secret ⇒ false (fail-closed).</summary>
    public static bool IsValidSignature(byte[] body, string secret, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader))
            return false;

        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        byte[] expected;
        try { expected = Convert.FromHexString(signatureHeader[prefix.Length..]); }
        catch (FormatException) { return false; }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = hmac.ComputeHash(body);

        return expected.Length == computed.Length && CryptographicOperations.FixedTimeEquals(computed, expected);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubWebhookTests`
Expected: PASS (7 Tests gesamt in der Klasse).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Git/GitHub/GitHubWebhook.cs tests/Naudit.Tests/GitHubWebhookTests.cs
git commit -m "feat(infra): verify GitHub webhook HMAC-SHA256 signature"
```

---

### Task 3: GitHubPlatform (REST-Client)

**Files:**
- Create: `src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs`
- Test: `tests/Naudit.Tests/GitHubPlatformTests.cs`

- [ ] **Step 1: Write the failing test**

`tests/Naudit.Tests/GitHubPlatformTests.cs`:
```csharp
using System.Net;
using System.Text;
using Naudit.Core.Models;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitHubPlatformTests
{
    private static readonly ReviewRequest Request = new("octo/hello-world", 42, "Title");

    private static HttpClient ClientReturning(HttpStatusCode status, string json, StubHttpMessageHandler? capture = null)
    {
        var handler = capture ?? new StubHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        return new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
    }

    [Fact]
    public async Task GetChangesAsync_mapsFilesFromApi_andSkipsFilesWithoutPatch()
    {
        const string json = """
        [
          { "filename": "src/Foo.cs", "patch": "@@ +1 @@\n+x" },
          { "filename": "assets/logo.png" }
        ]
        """;
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.OK, json));

        var changes = await platform.GetChangesAsync(Request);

        var change = Assert.Single(changes);
        Assert.Equal("src/Foo.cs", change.FilePath);
        Assert.Contains("+x", change.Diff);
    }

    [Fact]
    public async Task GetChangesAsync_requestsPullFilesUrl()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        });
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.OK, "[]", capture));

        await platform.GetChangesAsync(Request);

        Assert.Contains("repos/octo/hello-world/pulls/42/files", capture.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task PostSummaryAsync_postsIssueCommentWithBody()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.Created, "", capture));

        await platform.PostSummaryAsync(Request, "## Naudit Review");

        Assert.Equal(HttpMethod.Post, capture.LastRequest!.Method);
        Assert.Contains("repos/octo/hello-world/issues/42/comments", capture.LastRequest.RequestUri!.ToString());
        Assert.Contains("Naudit Review", capture.LastRequestBody!);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubPlatformTests`
Expected: Build-Fehler — `GitHubPlatform` existiert nicht (CS0246).

- [ ] **Step 3: Implement GitHubPlatform**

`src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs`:
```csharp
using System.Net.Http.Json;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git.GitHub;

/// <summary>IGitPlatform-Implementierung für GitHub. BaseAddress + Auth-Header kommen vom typed HttpClient.</summary>
public sealed class GitHubPlatform(HttpClient http) : IGitPlatform
{
    public async Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default)
    {
        // ProjectId enthält "owner/repo". Eine Seite (per_page=100) reicht für normale PRs (bewusste POC-Grenze).
        var url = $"repos/{request.ProjectId}/pulls/{request.MergeRequestIid}/files?per_page=100";
        var files = await http.GetFromJsonAsync<List<GitHubFile>>(url, ct);
        if (files is null)
            return [];

        // Dateien ohne patch (binär/zu groß) überspringen.
        return files
            .Where(f => !string.IsNullOrEmpty(f.Patch))
            .Select(f => new CodeChange(f.Filename, f.Patch!))
            .ToList();
    }

    public async Task PostSummaryAsync(ReviewRequest request, string markdown, CancellationToken ct = default)
    {
        // PR-Kommentar = Issue-Kommentar (gleiche Nummer).
        var url = $"repos/{request.ProjectId}/issues/{request.MergeRequestIid}/comments";
        var response = await http.PostAsJsonAsync(url, new { body = markdown }, ct);
        response.EnsureSuccessStatusCode();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubPlatformTests`
Expected: PASS (3 Tests).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs tests/Naudit.Tests/GitHubPlatformTests.cs
git commit -m "feat(infra): add GitHubPlatform REST client (pull files + issue comment)"
```

---

### Task 4: Plattform-Auswahl per Config + DI-Verdrahtung

**Files:**
- Create: `src/Naudit.Infrastructure/Git/GitOptions.cs`
- Create: `src/Naudit.Infrastructure/Git/GitHub/GitHubOptions.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`
- Modify: `src/Naudit.Web/appsettings.json`

- [ ] **Step 1: Create the option types**

`src/Naudit.Infrastructure/Git/GitOptions.cs`:
```csharp
namespace Naudit.Infrastructure.Git;

/// <summary>Welche Git-Plattform aktiv ist (per Config gewählt, eine pro Deployment).</summary>
public enum GitPlatformKind { GitLab, GitHub }

public sealed class GitOptions
{
    public GitPlatformKind Platform { get; set; } = GitPlatformKind.GitLab;
}
```

`src/Naudit.Infrastructure/Git/GitHub/GitHubOptions.cs`:
```csharp
namespace Naudit.Infrastructure.Git.GitHub;

public sealed class GitHubOptions
{
    public string BaseUrl { get; set; } = "https://api.github.com";  // GitHub REST-API
    public string Token { get; set; } = "";                           // PAT mit Repo-Zugriff (Bearer)
    public string WebhookSecret { get; set; } = "";                   // HMAC-Secret für X-Hub-Signature-256
}
```

- [ ] **Step 2: Wire the platform switch in DependencyInjection**

In `src/Naudit.Infrastructure/DependencyInjection.cs` die Usings ergänzen:
```csharp
using System.Net.Http.Headers;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
```

Den GitLab-Block (aktuell ab Kommentar `// GitLab: typed HttpClient ...` bis zur schließenden `});`) **ersetzen** durch:
```csharp
        // Git-Plattform: eine pro Deployment, per Config gewählt (analog zum AI-Provider).
        var gitOptions = configuration.GetSection("Naudit:Git").Get<GitOptions>() ?? new GitOptions();
        services.AddSingleton(gitOptions);

        switch (gitOptions.Platform)
        {
            case GitPlatformKind.GitHub:
                services.Configure<GitHubOptions>(configuration.GetSection("Naudit:GitHub"));
                services.AddHttpClient<IGitPlatform, GitHubPlatform>((sp, http) =>
                {
                    var opt = sp.GetRequiredService<IOptions<GitHubOptions>>().Value;
                    http.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opt.Token);
                    http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                    http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Naudit"); // GitHub verlangt einen User-Agent
                });
                break;

            default: // GitPlatformKind.GitLab
                services.Configure<GitLabOptions>(configuration.GetSection("Naudit:GitLab"));
                services.AddHttpClient<IGitPlatform, GitLabPlatform>((sp, http) =>
                {
                    var opt = sp.GetRequiredService<IOptions<GitLabOptions>>().Value;
                    http.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                    http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", opt.Token);
                });
                break;
        }
```

- [ ] **Step 3: Add config defaults to appsettings.json**

In `src/Naudit.Web/appsettings.json` innerhalb des `"Naudit"`-Objekts **vor** `"GitLab"` ergänzen:
```json
    "Git": {
      "Platform": "GitLab"
    },
```
und **nach** dem `"GitLab"`-Block ergänzen:
```json
    "GitHub": {
      "BaseUrl": "https://api.github.com",
      "Token": "",
      "WebhookSecret": ""
    },
```
(Auf gültiges JSON achten: Komma nach dem `"GitHub"`-Block, da `"Review"` folgt.)

- [ ] **Step 4: Verify the whole suite still builds and passes (GitLab-Default unverändert)**

Run: `dotnet test Naudit.slnx`
Expected: PASS — alle bestehenden Tests grün, neue Typen kompilieren.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Git/GitOptions.cs src/Naudit.Infrastructure/Git/GitHub/GitHubOptions.cs src/Naudit.Infrastructure/DependencyInjection.cs src/Naudit.Web/appsettings.json
git commit -m "feat(infra): select git platform via Naudit:Git:Platform (GitLab|GitHub)"
```

---

### Task 5: Webhook-Endpoint /webhook/github (nur bei aktiver GitHub-Plattform)

**Files:**
- Modify: `src/Naudit.Web/Program.cs`
- Test: `tests/Naudit.Tests/WebhookEndpointTests.cs` (Tests ergänzen)

- [ ] **Step 1: Write the failing tests (in WebhookEndpointTests ergänzen)**

Oben in `tests/Naudit.Tests/WebhookEndpointTests.cs` die Usings ergänzen:
```csharp
using System.Security.Cryptography;
using System.Text;
```

Innerhalb der Klasse `WebhookEndpointTests` ergänzen:
```csharp
    private static string SignGitHub(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public async Task GitHubWebhook_withMissingSignature_returnsUnauthorized()
    {
        var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Naudit:Git:Platform", "GitHub");
                b.UseSetting("Naudit:GitHub:WebhookSecret", "gh-secret");
            })
            .CreateClient();

        var response = await client.PostAsJsonAsync("/webhook/github", new { action = "opened" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GitHubWebhook_withValidSignature_andNonPullRequestEvent_returnsOk()
    {
        var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Naudit:Git:Platform", "GitHub");
                b.UseSetting("Naudit:GitHub:WebhookSecret", "gh-secret");
            })
            .CreateClient();

        const string body = """{ "zen": "Keep it simple." }""";
        var message = new HttpRequestMessage(HttpMethod.Post, "/webhook/github")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("X-GitHub-Event", "ping");
        message.Headers.Add("X-Hub-Signature-256", SignGitHub("gh-secret", body));

        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~GitHubWebhook"`
Expected: FAIL — `/webhook/github` ist nicht gemappt (404 statt 401/200).

- [ ] **Step 3: Implement conditional endpoint mapping**

In `src/Naudit.Web/Program.cs` die Usings ergänzen:
```csharp
using System.Text.Json;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
```

Den bestehenden `app.MapPost("/webhook/gitlab", ...)`-Block (von `app.MapPost("/webhook/gitlab"` bis zur abschließenden `});`) **ersetzen** durch:
```csharp
// Nur den Webhook-Endpoint der aktiven Plattform mappen.
var platform = app.Services.GetRequiredService<GitOptions>().Platform;

if (platform == GitPlatformKind.GitHub)
{
    app.MapPost("/webhook/github", async (HttpContext context, IReviewQueue queue, IOptions<GitHubOptions> gitHubOptions) =>
    {
        // Rohen Body lesen (Bytes) — die HMAC-Signatur geht über die exakten Bytes.
        using var ms = new MemoryStream();
        await context.Request.Body.CopyToAsync(ms);
        var rawBody = ms.ToArray();

        var signature = context.Request.Headers["X-Hub-Signature-256"].ToString();
        if (!GitHubWebhook.IsValidSignature(rawBody, gitHubOptions.Value.WebhookSecret, signature))
            return Results.Unauthorized();

        var eventType = context.Request.Headers["X-GitHub-Event"].ToString();
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(rawBody);
        if (payload is null)
            return Results.Ok();

        var request = GitHubWebhook.ToReviewRequest(eventType, payload);
        if (request is null)
            return Results.Ok(); // kein pull_request-Event oder keine reviewbare Aktion

        await queue.EnqueueAsync(request);
        return Results.Ok();
    });
}
else // GitPlatformKind.GitLab
{
    app.MapPost("/webhook/gitlab", async (HttpContext context, IReviewQueue queue, IOptions<GitLabOptions> gitLabOptions) =>
    {
        var secret = gitLabOptions.Value.WebhookSecret;
        var token = context.Request.Headers["X-Gitlab-Token"].ToString();
        if (string.IsNullOrEmpty(secret) || token != secret)
            return Results.Unauthorized();

        var payload = await context.Request.ReadFromJsonAsync<GitLabWebhookPayload>();
        if (payload is null)
            return Results.Ok();

        var request = GitLabWebhook.ToReviewRequest(payload);
        if (request is null)
            return Results.Ok(); // kein MR-Event oder keine reviewbare Aktion

        await queue.EnqueueAsync(request);
        return Results.Ok();
    });
}
```

- [ ] **Step 4: Run the full suite to verify everything passes**

Run: `dotnet test Naudit.slnx`
Expected: PASS — neue GitHub-Endpoint-Tests grün; bestehende GitLab-Endpoint-Tests (Default-Plattform) weiterhin grün.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Web/Program.cs tests/Naudit.Tests/WebhookEndpointTests.cs
git commit -m "feat(web): add /webhook/github endpoint gated by active platform"
```

---

### Task 6: Doku — README, Repo + Vault

**Files:**
- Modify: `README.md`
- Modify: `CLAUDE.md` (Extension-Point-Notiz aktualisieren)
- Modify: `/home/bnau/workspace/BenediktsMind/1. Projects/Naudit/Naudit – Architektur.md`
- Modify: `/home/bnau/workspace/BenediktsMind/1. Projects/Naudit/Doings.md`

- [ ] **Step 1: README — GitHub-Setup ergänzen**

In `README.md` einen Abschnitt „GitHub" ergänzen (analog zum GitLab-Abschnitt): Config-Schalter `Naudit:Git:Platform=GitHub`, Webhook auf `…/webhook/github` mit Content-Type `application/json` + Secret, Events „Pull requests", PAT mit Repo-Zugriff. Beispiel:
```bash
dotnet user-secrets set "Naudit:Git:Platform"          "GitHub" --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitHub:Token"          "<fine-grained-PAT>" --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitHub:WebhookSecret"  "<random-secret>" --project src/Naudit.Web
```
Bekannte Grenze dokumentieren: nur die erste Seite der PR-Files (max. 100) wird gereviewt.

- [ ] **Step 2: CLAUDE.md — Extension-Point-Notiz aktualisieren**

In `CLAUDE.md` den Punkt „New git platform (e.g. GitHub)" so anpassen, dass GitHub jetzt umgesetzt ist: zweite `IGitPlatform`-Implementierung unter `Git/GitHub/`, Auswahl per `Naudit:Git:Platform` (eine pro Deployment), Webhook `/webhook/github` mit HMAC-SHA256.

- [ ] **Step 3: Vault — Architektur-Doku + Board aktualisieren**

In `Naudit – Architektur.md` §5 („Wie mit GitLab gesprochen wird") um einen Abschnitt „Wie mit GitHub gesprochen wird" ergänzen (Endpoint `/webhook/github`, HMAC-Signatur, REST `pulls/{n}/files` + `issues/{n}/comments`, PAT-Header) und in §6 die Config-Tabelle um `Git:Platform` und den `GitHub:*`-Block erweitern.

In `Doings.md` den „Doing"-Eintrag zu GitHub nach „Completed" verschieben:
```
- [x] GitHub-Support (Config-gewählt, PAT): zweite IGitPlatform-Impl + /webhook/github (HMAC) ✅ 2026-06-16
```

- [ ] **Step 4: Verify build + suite once more**

Run: `dotnet test Naudit.slnx`
Expected: PASS (Doku-Änderungen beeinflussen Tests nicht).

- [ ] **Step 5: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: document GitHub platform setup and selection"
```
(Vault-Dateien liegen außerhalb des Repos und werden nicht mit-committet.)

---

## Notes for the implementer

- **Solution-Datei ist `Naudit.slnx`**, nicht `.sln` — `dotnet test Naudit.sln` schlägt mit MSB1009 fehl.
- **MEAI-/Framework-API-Namen sind versionsabhängig.** `Convert.ToHexStringLower` (Tests) und `CryptographicOperations.FixedTimeEquals` sind in .NET 10 vorhanden.
- **`Naudit.Core` nicht anfassen.** GitHub lebt komplett in `Naudit.Infrastructure` + Verdrahtung in `Naudit.Web`.
- **`ReviewRequest` wird nicht umbenannt:** für GitHub hält `ProjectId` ein `owner/repo`, `MergeRequestIid` die PR-Nummer.
- Code-Kommentare auf Deutsch (Projektkonvention).
