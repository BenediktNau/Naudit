# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Naudit is a self-hosted .NET code-review bot (POC/MVP). It receives GitLab merge-request
webhooks, has an LLM review the MR diff via Microsoft.Extensions.AI (MEAI), and posts a single
summary Markdown comment back to the MR. The AI provider is swappable by configuration alone.

## Commands

The solution file is `Naudit.slnx` (the XML solution format) — **not** `Naudit.sln`.
`dotnet test Naudit.sln` fails with MSB1009; always use `Naudit.slnx`.

```bash
dotnet build Naudit.slnx
dotnet test  Naudit.slnx                 # full suite

# Run the host (webhook on /webhook/gitlab, liveness on /health)
dotnet run --project src/Naudit.Web

# Single test class
dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests

# Single test method
dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~ReviewAsync_postsModelOutput_asSummary"
```

Config secrets are set via user-secrets on the Web project, never in `appsettings.json`:

```bash
dotnet user-secrets set "Naudit:GitLab:Token" "..." --project src/Naudit.Web
```

## Architecture

Three projects with a strict, deliberate dependency direction:
`Web → Infrastructure → Core`, and `Core → MEAI abstractions only`.

- **`Naudit.Core`** — domain records (`ReviewRequest`, `CodeChange`), orchestration
  (`ReviewService`, `PromptBuilder`, `ReviewOptions`) and abstractions (`IGitPlatform`).
  **The central rule: Core depends only on `Microsoft.Extensions.AI.Abstractions`. It knows no
  concrete LLM provider and no git platform.** Everything Core needs from the outside world is
  expressed as two interfaces: `IChatClient` (from MEAI) and `IGitPlatform`. Keep provider/platform
  SDKs out of this project.
- **`Naudit.Infrastructure`** — all SDK/HTTP implementations: the AI provider factory
  (`Ai/AiClientFactory.cs`) and the GitLab client (`Git/GitLab/`). Composition lives in
  `DependencyInjection.cs` (`AddNauditInfrastructure`), which reads config and registers
  `IChatClient`, the typed GitLab `HttpClient`, `ReviewOptions`, and `ReviewService`.
- **`Naudit.Web`** — ASP.NET Minimal API host. The webhook endpoint validates the secret,
  maps the payload, **enqueues and returns `200` immediately**, then a `ReviewBackgroundService`
  drains a `Channel`-based `ReviewQueue` and runs each review in its own DI scope. This avoids
  GitLab's webhook timeout.

### Request flow

`GitLab webhook → /webhook/gitlab (validate + enqueue, 200) → ReviewQueue → ReviewBackgroundService
→ ReviewService` which: `IGitPlatform.GetChangesAsync` → `PromptBuilder.Build` → `IChatClient.GetResponseAsync`
→ `IGitPlatform.PostSummaryAsync`. If there are no changes, nothing is posted.

### Extension points (do not break the Core rule)

- **New AI provider:** add a case to the `switch` in `AiClientFactory.Create` returning an
  `IChatClient`. NVIDIA/other OpenAI-compatible endpoints reuse the OpenAI client with a custom
  `Endpoint` — no dedicated adapter. Selection is config-only via `Naudit:Ai:Provider`.
- **New git platform (e.g. GitHub):** add a second `IGitPlatform` implementation in Infrastructure
  and wire it in `AddNauditInfrastructure`. No change to Core.

## Conventions & gotchas

- **TDD workflow.** Work follows `docs/superpowers/plans/2026-06-16-naudit-codereview-bot.md`
  (11 tasks, red → green → one commit per task). Code comments are in German.
- **MEAI GA API names** are used and are version-sensitive: `IChatClient.GetResponseAsync`,
  `ChatResponse.Text`, `.AsIChatClient()` (OpenAI bridge), `new AnthropicClient(key).Messages`.
  A missing-member build error at these spots usually means a package-version mismatch, not a logic bug.
- **.NET 10 specifics:** `public partial class Program {}` is intentionally absent — the generated
  `Program` is already public, so `WebApplicationFactory<Program>` works without it (analyzer ASP0027).
- `ReviewRequest.MergeRequestIid` and the GitLab DTO `Iid` are `int` (the plan text says `long`;
  the code uses `int` because GitLab's `iid` is project-scoped and small).
- **Known cosmetic cruft:** `src/Naudit.Core/Review/PromtBuilder.cs` and
  `tests/Naudit.Tests/PromtBuilderTests.cs` have a filename typo ("Promt"); the class is correctly
  named `PromptBuilder`. `tests/Naudit.Tests/UnitTest1.cs` is a leftover xUnit template test.

## Testing approach

Core is tested with no network via `Fakes/FakeChatClient` and `Fakes/FakeGitPlatform`. The GitLab
HTTP client is tested with `Fakes/StubHttpMessageHandler` (asserts URL + body). The webhook endpoint
is tested with `WebApplicationFactory<Program>` on paths that never reach the LLM or GitLab
(401 path, non-MR-event path). The real end-to-end path is verified manually (Task 11 in the plan).
