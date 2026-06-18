# Architecture & status

## How it works

```text
GitLab/GitHub ──webhook──▶  /webhook/gitlab|github  ──▶  Queue  ──▶  BackgroundService
                                 (200 immediately)                          │
                                                                            ▼
   platform comment    ◀──  ReviewService  ──▶  PromptBuilder ──▶ IChatClient (LLM)
                                  │  └─ fetches the diff via IGitPlatform
                                  └─ posts the summary via IGitPlatform
```

## Project layout

| Project | Responsibility |
| --- | --- |
| `Naudit.Core` | Domain, orchestration (`ReviewService`, `PromptBuilder`), abstractions (`IGitPlatform`). Depends only on the MEAI abstractions — knows no concrete provider and no platform. |
| `Naudit.Infrastructure` | Provider factory (`AiClientFactory`) + platform implementations (`GitLabPlatform`, `GitHubPlatform`) + DI composition (`AddNauditInfrastructure`). |
| `Naudit.Web` | ASP.NET Minimal API host: accepts the webhook, returns `200` immediately, and processes the review asynchronously in a `BackgroundService` (channel queue). Only the active platform's endpoint is mapped. Additionally a synchronous `POST /review` as a merge gate (see [CI integration](ci-integration.md)). |

## Tests

```bash
dotnet test Naudit.slnx                                                          # all tests
dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests   # one class
```

The suite tests Core with no network (fakes for LLM/GitLab), the GitLab/GitHub clients
against a stub `HttpMessageHandler`, and the webhook endpoint via `WebApplicationFactory` —
without hitting a real LLM or Git platform.

## Roadmap

Deliberately not (yet) part of the MVP:

- Inline / positional comments instead of just a summary
- Idempotency / de-duplication of repeated events
- Diff-size limiting for large MRs
- Per-repo rules (`.naudit.yml`)

## Known limitations (POC)

- No idempotency: a resent webhook event triggers another review.
- No retries or timeouts on the HTTP clients (Git platform / LLM).
- The queue is in-memory: reviews not yet processed are lost on process restart.
- The entire diff is sent to the LLM (no size cap) — token/cost risk on very large MRs/PRs.
- GitHub: only the **first 100 changed files** are reviewed; larger PRs are silently truncated.
- A webhook request with a valid signature but malformed JSON body returns HTTP 500 (no dedicated error handling — parity with the GitLab endpoint).
- The review prompt says "Merge Request" even for GitHub PRs (cosmetic; intentionally not generalized).
