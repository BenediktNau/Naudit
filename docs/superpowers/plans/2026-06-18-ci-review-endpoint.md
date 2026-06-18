# CI-Review-Endpoint Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Naudit über die CI/CD-Pipeline ansteuerbar machen: ein synchroner, token-authentifizierter `POST /review`-Endpoint, der den genannten MR/PR inline reviewt, den Kommentar postet und ein strukturiertes Verdict (`approve` | `request_changes`) zurückgibt, damit der Pipeline-Job den Merge gaten kann.

**Architecture:** Der Review-Kern bleibt unverändert; neu ist eine zweite Auslöser-Quelle. `ReviewService.ReviewAsync` liefert künftig ein `ReviewResult(Markdown, Verdict)`. Das Verdict kommt per **MEAI Structured Output**, realisiert Core-rein über `ChatResponseFormat.Json` (in `Microsoft.Extensions.AI.Abstractions`) + manuelle JSON-Deserialisierung — die Extension `GetResponseAsync<T>` wird **nicht** verwendet, da sie das Nicht-Abstractions-Paket erfordern und die Core-Regel brechen würde. Der neue Endpoint in `Naudit.Web` führt den Review inline im Request-Scope aus (umgeht die `ReviewQueue`); der Webhook-Pfad bleibt 1:1.

**Tech Stack:** .NET 10, ASP.NET Minimal API, Microsoft.Extensions.AI(.Abstractions) 10.7, xUnit, `WebApplicationFactory<Program>`, System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-06-18-ci-review-endpoint-design.md`

---

## File Structure

- **Create** `src/Naudit.Core/Models/ReviewResult.cs` — `enum ReviewVerdict` + `record ReviewResult`. Einzige Verantwortung: das Review-Ergebnis als Domänentyp.
- **Modify** `src/Naudit.Core/Review/PromtBuilder.cs` — `DefaultSystemPrompt` weist das Modell an, JSON `{summary, verdict}` zu liefern.
- **Modify** `src/Naudit.Core/Review/ReviewService.cs` — gibt `ReviewResult` zurück; JSON-Mode + Deserialisierung + Verdict-Mapping. Enthält das private Wire-DTO `LlmReviewResponse`.
- **Modify** `tests/Naudit.Tests/ReviewServiceTests.cs` — Tests auf strukturierte Antwort + Verdict umstellen/erweitern.
- **Modify** `src/Naudit.Web/Program.cs` — `POST /review` (immer gemappt), Token-Check, `ReviewTriggerRequest`-DTO.
- **Create** `tests/Naudit.Tests/ReviewEndpointTests.cs` — 401- und Happy-Path-Test mit Fakes via `WebApplicationFactory`.
- **Create** `docs/ci-integration.md` — GitLab-CI- und GitHub-Actions-Snippet.
- **Modify** `CLAUDE.md` — kurzer Hinweis auf den `/review`-Endpoint.
- **Modify** `~/workspace/BenediktsMind/1. Projects/Naudit/Doings.md` + **Create** Vault-Design-Notiz.

---

## Task 1: ReviewResult-Modell (Core)

**Files:**
- Create: `src/Naudit.Core/Models/ReviewResult.cs`

- [ ] **Step 1: Modell anlegen**

```csharp
namespace Naudit.Core.Models;

/// <summary>Maschinenlesbares Urteil eines Reviews — Basis fürs CI-Gate.</summary>
public enum ReviewVerdict { Approve, RequestChanges }

/// <summary>Ergebnis eines Reviews: der geposteter Markdown-Text plus das Urteil.</summary>
public sealed record ReviewResult(string Markdown, ReviewVerdict Verdict);
```

- [ ] **Step 2: Build prüfen**

Run: `dotnet build Naudit.slnx`
Expected: Build erfolgreich (das bestehende `ReviewService` liefert noch `Task`, kompiliert weiter — nur ein neuer Typ kam hinzu).

- [ ] **Step 3: Commit**

```bash
git add src/Naudit.Core/Models/ReviewResult.cs
git commit -m "feat(core): add ReviewResult + ReviewVerdict for CI gating"
```

---

## Task 2: ReviewService liefert strukturiertes Verdict (TDD, Core)

**Files:**
- Modify: `src/Naudit.Core/Review/PromtBuilder.cs`
- Modify: `src/Naudit.Core/Review/ReviewService.cs`
- Test: `tests/Naudit.Tests/ReviewServiceTests.cs`

- [ ] **Step 1: Tests umschreiben/erweitern (failing)**

Ersetze den **gesamten** Inhalt von `tests/Naudit.Tests/ReviewServiceTests.cs` durch:

```csharp
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ReviewServiceTests
{
    private static readonly ReviewRequest Request = new("1", 42, "Title");

    [Fact]
    public async Task ReviewAsync_postsSummary_andReturnsApprove()
    {
        var chat = new FakeChatClient("""{"summary":"## Review\n- looks fine","verdict":"approve"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        var result = await service.ReviewAsync(Request);

        Assert.Equal("## Review\n- looks fine", git.PostedMarkdown);
        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
        Assert.Equal("SYS", chat.LastMessages![0].Text);
    }

    [Fact]
    public async Task ReviewAsync_returnsRequestChanges_whenModelSaysSo()
    {
        var chat = new FakeChatClient("""{"summary":"## Review\n- bug here","verdict":"request_changes"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.RequestChanges, result.Verdict);
        Assert.Equal("## Review\n- bug here", git.PostedMarkdown);
    }

    [Fact]
    public async Task ReviewAsync_withNoChanges_postsNothing_andApproves()
    {
        var chat = new FakeChatClient("unused");
        var git = new FakeGitPlatform([]);
        var service = new ReviewService(chat, git, new ReviewOptions());

        var result = await service.ReviewAsync(Request);

        Assert.Equal(0, git.PostCallCount);
        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
    }
}
```

- [ ] **Step 2: Test ausführen, Fehlschlag bestätigen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests`
Expected: Build-Fehler bzw. FAIL — `ReviewAsync` gibt heute `Task` (kein `ReviewResult`) zurück, `result.Verdict` existiert noch nicht.

- [ ] **Step 3: DefaultSystemPrompt auf JSON-Ausgabe umstellen**

In `src/Naudit.Core/Review/PromtBuilder.cs` die Konstante `DefaultSystemPrompt` ersetzen durch:

```csharp
    public const string DefaultSystemPrompt =
        "You are Naudit, a senior code reviewer. Review the merge request diff below. " +
        "Focus on correctness bugs, security issues and clear maintainability problems. Be concise. " +
        "Respond ONLY with a JSON object with exactly two fields: " +
        "\"summary\" - GitHub-flavored Markdown (a one-line summary followed by a bullet list of findings; " +
        "if there are no significant issues, say so briefly) - and " +
        "\"verdict\" - either \"approve\" or \"request_changes\" " +
        "(use \"request_changes\" only when there are correctness or security bugs that should block the merge).";
```

(`PromtBuilderTests` bleibt grün: dort wird ein eigener Marker-Prompt übergeben, nicht `DefaultSystemPrompt`.)

- [ ] **Step 4: ReviewService implementieren**

Ersetze den **gesamten** Inhalt von `src/Naudit.Core/Review/ReviewService.cs` durch:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

public sealed class ReviewService(IChatClient chatClient, IGitPlatform gitPlatform, ReviewOptions options)
{
    // Web-Defaults: camelCase + case-insensitive — passt zu den JSON-Feldern summary/verdict.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var changes = await gitPlatform.GetChangesAsync(request, ct);
        if (changes.Count == 0)
            return new ReviewResult(string.Empty, ReviewVerdict.Approve);

        var messages = PromptBuilder.Build(options.SystemPrompt, request, changes);

        // Structured Output Core-rein: JSON-Mode (in MEAI.Abstractions), Deserialisierung selbst.
        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        var response = await chatClient.GetResponseAsync(messages, chatOptions, ct);

        var parsed = JsonSerializer.Deserialize<LlmReviewResponse>(response.Text, JsonOpts)
            ?? throw new InvalidOperationException("LLM lieferte keine parsebare Review-Antwort.");

        var verdict = string.Equals(parsed.Verdict, "request_changes", StringComparison.OrdinalIgnoreCase)
            ? ReviewVerdict.RequestChanges
            : ReviewVerdict.Approve;

        await gitPlatform.PostSummaryAsync(request, parsed.Summary, ct);
        return new ReviewResult(parsed.Summary, verdict);
    }

    // Wire-DTO für die LLM-Antwort. Verdict bewusst als string (kein Enum),
    // um Enum-JSON-Fragilität zu vermeiden; Mapping erfolgt oben.
    private sealed record LlmReviewResponse(string Summary, string Verdict);
}
```

- [ ] **Step 5: Tests ausführen, Erfolg bestätigen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests`
Expected: PASS (3 Tests).

- [ ] **Step 6: Volle Suite — ReviewBackgroundService kompiliert weiter**

Run: `dotnet test Naudit.slnx`
Expected: PASS. `ReviewBackgroundService` ruft `await reviewService.ReviewAsync(...)` und ignoriert den neuen Rückgabewert — kein Change nötig.

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Core/Review/ReviewService.cs src/Naudit.Core/Review/PromtBuilder.cs tests/Naudit.Tests/ReviewServiceTests.cs
git commit -m "feat(core): ReviewService returns structured verdict via JSON response format"
```

---

## Task 3: POST /review Endpoint (TDD, Web)

**Files:**
- Modify: `src/Naudit.Web/Program.cs`
- Test: `tests/Naudit.Tests/ReviewEndpointTests.cs` (neu)

- [ ] **Step 1: Endpoint-Tests schreiben (failing)**

Lege `tests/Naudit.Tests/ReviewEndpointTests.cs` an:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ReviewEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReviewEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Review_withWrongToken_returnsUnauthorized()
    {
        var client = _factory
            .WithWebHostBuilder(b => b.UseSetting("Naudit:GitLab:WebhookSecret", "test-secret"))
            .CreateClient();

        var message = new HttpRequestMessage(HttpMethod.Post, "/review")
        {
            Content = JsonContent.Create(new { projectId = "1", mergeRequestIid = 42, title = "T" }),
        };
        message.Headers.Add("X-Naudit-Token", "wrong");

        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Review_withValidToken_runsReview_andReturnsVerdict()
    {
        var fakeChat = new FakeChatClient("""{"summary":"## Review\n- bug","verdict":"request_changes"}""");
        var fakeGit = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);

        var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Naudit:GitLab:WebhookSecret", "test-secret");
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IChatClient>();
                    services.AddSingleton<IChatClient>(fakeChat);
                    services.RemoveAll<IGitPlatform>();
                    services.AddSingleton<IGitPlatform>(fakeGit);
                });
            })
            .CreateClient();

        var message = new HttpRequestMessage(HttpMethod.Post, "/review")
        {
            Content = JsonContent.Create(new { projectId = "1", mergeRequestIid = 42, title = "T" }),
        };
        message.Headers.Add("X-Naudit-Token", "test-secret");

        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VerdictBody>();
        Assert.Equal("request_changes", body!.Verdict);
        Assert.Equal("## Review\n- bug", fakeGit.PostedMarkdown);
    }

    private sealed record VerdictBody(string Verdict);
}
```

- [ ] **Step 2: Test ausführen, Fehlschlag bestätigen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewEndpointTests`
Expected: FAIL — `/review` ist noch nicht gemappt (Wrong-Token-Test bekäme 404 statt 401, Happy-Path 404).

- [ ] **Step 3: Endpoint implementieren**

In `src/Naudit.Web/Program.cs`:

(a) Oben die zusätzlichen `using`s ergänzen (zu den bestehenden hinzufügen):

```csharp
using System.Security.Cryptography;
using System.Text;
using Naudit.Core.Models;
using Naudit.Core.Review;
```

(b) **Vor** der Zeile `app.Run();` einfügen:

```csharp
// CI/CD-Trigger: synchroner Review mit strukturiertem Verdict (Merge-Gate).
// Immer gemappt, unabhängig von der aktiven Plattform. Auth = Webhook-Secret als Header-Token.
app.MapPost("/review", async (
    HttpContext context,
    ReviewTriggerRequest body,
    GitOptions gitOptions,
    IOptions<GitLabOptions> gitLabOptions,
    IOptions<GitHubOptions> gitHubOptions,
    CancellationToken ct) =>
{
    var secret = gitOptions.Platform == GitPlatformKind.GitHub
        ? gitHubOptions.Value.WebhookSecret
        : gitLabOptions.Value.WebhookSecret;

    var token = context.Request.Headers["X-Naudit-Token"].ToString();
    if (!IsValidNauditToken(secret, token))
        return Results.Unauthorized();

    // ReviewService erst nach bestandener Auth auflösen (Scope-Service, inline statt Queue).
    var reviewService = context.RequestServices.GetRequiredService<ReviewService>();
    var request = new ReviewRequest(body.ProjectId, body.MergeRequestIid, body.Title ?? string.Empty);
    var result = await reviewService.ReviewAsync(request, ct);

    var verdict = result.Verdict == ReviewVerdict.RequestChanges ? "request_changes" : "approve";
    return Results.Ok(new { verdict });
});

// Konstant-zeitlicher Vergleich; leeres Secret oder leerer Token ⇒ false (fail-closed).
static bool IsValidNauditToken(string? secret, string? provided)
{
    if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(provided))
        return false;
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(provided));
}
```

(c) **Nach** `app.Run();` (zu den Typdeklarationen am Dateiende) ergänzen:

```csharp
/// <summary>Request-Body des CI-Triggers; wird direkt auf ReviewRequest gemappt
/// (bei GitHub ist ProjectId = "owner/repo" und MergeRequestIid = PR-Nummer).</summary>
public sealed record ReviewTriggerRequest(string ProjectId, int MergeRequestIid, string? Title);
```

- [ ] **Step 4: Tests ausführen, Erfolg bestätigen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewEndpointTests`
Expected: PASS (2 Tests).

- [ ] **Step 5: Volle Suite**

Run: `dotnet test Naudit.slnx`
Expected: PASS (alle bestehenden Webhook-Tests bleiben grün — `/review` ist additiv).

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Web/Program.cs tests/Naudit.Tests/ReviewEndpointTests.cs
git commit -m "feat(web): add synchronous POST /review endpoint with token auth and verdict body"
```

---

## Task 4: CI-Integration dokumentieren

**Files:**
- Create: `docs/ci-integration.md`
- Modify: `CLAUDE.md`

- [ ] **Step 1: `docs/ci-integration.md` anlegen**

```markdown
# CI/CD-Integration über `POST /review`

Alternativ (oder ergänzend) zum Webhook kann eine Pipeline Naudit aktiv triggern. Der Endpoint
läuft **synchron**: er reviewt den MR/PR inline, postet den Kommentar und antwortet mit dem Verdict.

- **Auth:** Header `X-Naudit-Token`, verglichen gegen das `WebhookSecret` der aktiven Plattform
  (`Naudit:GitLab:WebhookSecret` bzw. `Naudit:GitHub:WebhookSecret`). In der CI als maskierte
  Variable hinterlegen.
- **Body:** `{ "projectId": "<id|owner/repo>", "mergeRequestIid": <int>, "title": "<text>" }`.
- **Antwort:** `200` mit `{ "verdict": "approve" | "request_changes" }`. Auth-Fehler ⇒ `401`,
  Naudit-/LLM-/Git-Fehler ⇒ `5xx`. Der Job failt bei `request_changes` **und** bei non-2xx
  (`curl -f` deckt Letzteres ab).

## GitLab CI (`.gitlab-ci.yml`)

\`\`\`yaml
naudit-review:
  stage: test
  image: alpine:latest
  rules:
    - if: $CI_PIPELINE_SOURCE == "merge_request_event"
  before_script:
    - apk add --no-cache curl jq
  script:
    - |
      VERDICT=$(curl -sf -X POST "$NAUDIT_URL/review" \
        -H "X-Naudit-Token: $NAUDIT_TOKEN" -H "Content-Type: application/json" \
        -d "{\"projectId\":\"$CI_PROJECT_ID\",\"mergeRequestIid\":$CI_MERGE_REQUEST_IID,\"title\":\"$CI_MERGE_REQUEST_TITLE\"}" \
        | jq -r '.verdict')
      echo "Naudit verdict: $VERDICT"
      [ "$VERDICT" = "approve" ]
\`\`\`

`NAUDIT_URL` und `NAUDIT_TOKEN` als CI/CD-Variablen setzen (Token maskiert).

## GitHub Actions

\`\`\`yaml
name: Naudit Review
on:
  pull_request:
    types: [opened, reopened, synchronize]
jobs:
  review:
    runs-on: ubuntu-latest
    steps:
      - name: Naudit code review
        env:
          NAUDIT_URL: ${{ secrets.NAUDIT_URL }}
          NAUDIT_TOKEN: ${{ secrets.NAUDIT_TOKEN }}
        run: |
          VERDICT=$(curl -sf -X POST "$NAUDIT_URL/review" \
            -H "X-Naudit-Token: $NAUDIT_TOKEN" -H "Content-Type: application/json" \
            -d "{\"projectId\":\"${{ github.repository }}\",\"mergeRequestIid\":${{ github.event.pull_request.number }},\"title\":\"${{ github.event.pull_request.title }}\"}" \
            | jq -r '.verdict')
          echo "Naudit verdict: $VERDICT"
          [ "$VERDICT" = "approve" ]
\`\`\`
```

> Hinweis beim Umsetzen: Die drei ` ```yaml `-Blöcke oben sind im Plan mit `\`\`\`` escaped, damit der Markdown-Codeblock des Plans nicht bricht. In der echten Datei `docs/ci-integration.md` als normale ` ``` `-Fences schreiben.

- [ ] **Step 2: Hinweis in `CLAUDE.md` ergänzen**

In `CLAUDE.md` im Abschnitt **Architecture → `Naudit.Web`** nach dem Satz, der mit
„This avoids webhook timeouts." endet, einen neuen Absatz anhängen:

```markdown
  Additionally, a synchronous `POST /review` endpoint (always mapped) lets a CI/CD pipeline trigger
  a review directly instead of via webhook: it authenticates an `X-Naudit-Token` header (constant-time)
  against the active platform's `WebhookSecret`, runs the review **inline** (bypassing the queue),
  and returns `{ "verdict": "approve" | "request_changes" }` so the job can gate the merge. See
  `docs/ci-integration.md`.
```

- [ ] **Step 3: Build/Tests unverändert grün (Doku-only)**

Run: `dotnet test Naudit.slnx`
Expected: PASS (keine Code-Änderung).

- [ ] **Step 4: Commit**

```bash
git add docs/ci-integration.md CLAUDE.md
git commit -m "docs: document CI/CD integration via POST /review"
```

---

## Task 5: BenediktsMind-Vault pflegen

**Files:**
- Modify: `~/workspace/BenediktsMind/1. Projects/Naudit/Doings.md`
- Create: `~/workspace/BenediktsMind/1. Projects/Naudit/2026-06-18 CI-Review-Endpoint – Design.md`

- [ ] **Step 1: Board aktualisieren**

In `Doings.md` im Abschnitt `## ✅ Completed` (als neueste Zeile am Ende der Liste) ergänzen:

```markdown
- [x] CI-Review-Endpoint: synchroner `POST /review` (X-Naudit-Token gegen Webhook-Secret, inline statt Queue), strukturiertes Verdict-Gate via MEAI Structured Output (`ChatResponseFormat.Json`, Core-rein), `ReviewResult`/`ReviewVerdict` in Core, Doku + TDD-Suite grün ([[2026-06-18 CI-Review-Endpoint – Design]]) ✅ 2026-06-18
```

- [ ] **Step 2: Design-Notiz im Vault anlegen**

Datei `~/workspace/BenediktsMind/1. Projects/Naudit/2026-06-18 CI-Review-Endpoint – Design.md` mit:

```markdown
---
projekt: Naudit
tags:
  - design
datum: 2026-06-18
---

# CI-Review-Endpoint – Design

Naudit ist jetzt zusätzlich zum Webhook über die **CI/CD-Pipeline** ansteuerbar.

## Kern
- Neuer **synchroner** `POST /review` (immer gemappt, parallel zum Webhook).
- Pipeline ruft den laufenden Service; kein eigenes CLI-Tool.
- **Auth:** Header `X-Naudit-Token`, konstant-zeitlich gegen das `WebhookSecret` der aktiven
  Plattform (kein HMAC, kein neuer Config-Key).
- **Inline** statt Queue: Endpoint führt `ReviewService.ReviewAsync` im Request-Scope aus.
- **Verdict-Gate:** `ReviewService` liefert `ReviewResult(Markdown, Verdict)`. Verdict via
  Structured Output — Core-rein über `ChatResponseFormat.Json` (MEAI.Abstractions) + manuelle
  JSON-Deserialisierung; `GetResponseAsync<T>` wird bewusst vermieden (würde Core-Regel brechen).
- **Antwort:** `200 { "verdict": "approve" | "request_changes" }`; keine Changes ⇒ `approve`;
  Auth ⇒ `401`; Infra-Fehler ⇒ `5xx`. Job failt bei `request_changes` und non-2xx.

## Bezug
- Spec im Repo: `docs/superpowers/specs/2026-06-18-ci-review-endpoint-design.md`
- Plan im Repo: `docs/superpowers/plans/2026-06-18-ci-review-endpoint.md`
- CI-Snippets: `docs/ci-integration.md`
- Architektur: [[Naudit – Architektur]]
```

- [ ] **Step 3: Vault committen (falls Git-Repo)**

```bash
git -C ~/workspace/BenediktsMind add "1. Projects/Naudit/Doings.md" "1. Projects/Naudit/2026-06-18 CI-Review-Endpoint – Design.md" && git -C ~/workspace/BenediktsMind commit -m "Naudit: CI-Review-Endpoint dokumentiert" || echo "Vault nicht versioniert oder nichts zu committen — übersprungen"
```

---

## Self-Review (vom Plan-Autor durchgeführt)

- **Spec-Abdeckung:** Endpoint (T3), Inline/Queue-Umgehung (T3), Auth via Webhook-Secret-Token (T3), strukturiertes Verdict (T1/T2), 200+Body-Konvention (T3), „keine Changes ⇒ approve" (T2), Pipeline-Snippets (T4), Vault-Pflege (T5) — alle Spec-Punkte haben einen Task. ✓
- **Core-Regel:** `ReviewService` nutzt nur `Microsoft.Extensions.AI.Abstractions` (`ChatResponseFormat.Json`, `ChatOptions`, `ChatResponse.Text`) + `System.Text.Json` — kein neues Paket in Core. ✓
- **Typ-Konsistenz:** `ReviewResult(Markdown, Verdict)`, `ReviewVerdict {Approve, RequestChanges}`, `LlmReviewResponse(Summary, Verdict)`, `ReviewTriggerRequest(ProjectId, MergeRequestIid, Title?)`, Header `X-Naudit-Token` — durchgängig identisch verwendet. ✓
- **Bestehende Tests:** `PromtBuilderTests` (eigener Marker-Prompt) und Webhook-Tests bleiben grün; `ReviewServiceTests` werden bewusst angepasst (Step T2.1). ✓
```
