# GitHub App auth (bot identity) + real review verdict — Design

*2026-07-02 · Naudit*

## Problem

Two related board items, one root cause:

- **"Register Naudit as a real reviewer"** — Naudit already posts through GitHub's Reviews API
  (`POST /pulls/{n}/reviews`, `GitHubPlatform`), but with a hard-coded `event = "COMMENT"`. It
  never sets an `APPROVE`/`REQUEST_CHANGES` review state, so it looks like a commenter, not a
  reviewer (unlike CodeRabbit).
- **Bot identity instead of the user account** — Naudit authenticates with a user PAT
  (`Naudit:GitHub:Token`), so every comment appears as *the user who owns the token*. Worse:
  GitHub rejects `APPROVE`/`REQUEST_CHANGES` submitted by the PR author (HTTP 422) — with the
  owner's PAT a real verdict is **impossible on the owner's own PRs**. A separate identity is a
  *prerequisite* for the first wish, not just cosmetics.

User priority (set explicitly): **professional and trivially integrable into any environment** —
adding Naudit to a new repo/org must be as close to one click as possible.

## Goal

Naudit can act as **`Naudit[bot]`** via a **GitHub App** (install-on-repo = one click, one central
webhook for all installations, short-lived installation tokens) and can submit a **real review
verdict** (`APPROVE` / `REQUEST_CHANGES`) derived from the existing severity-aware gate — both
opt-in, with today's behaviour as the untouched default.

## Key decisions

- **GitHub App is the flagship integration path.** One app, installed per repo/org with one
  click. No extra seat, no per-repo webhook (the app has a single central webhook), identity
  `Naudit[bot]`, and short-lived installation tokens (1 h) minted from an RS256 JWT — one secret
  (the app private key) regardless of how many repos.
- **Config-only selection, nothing breaks:** `Naudit:GitHub:Auth = Pat | App` (default **Pat** ⇒
  today's behaviour untouched). The PAT path stays as dev/fallback.
- **New `GitHubAppTokenProvider : IGitTokenProvider`** — same seam as
  `ConfiguredGitTokenProvider`, no Core involvement. JWT (RS256 via BCL `RSA.ImportFromPem`, no
  new NuGet package) → resolve installation (`GET /repos/{owner}/{repo}/installation`) → mint
  installation token (`POST /app/installations/{id}/access_tokens`), **cached** until ~5 min
  before expiry. Optional `InstallationId` config skips the lookup. The private key is accepted
  as raw PEM or base64-encoded PEM (env-var friendly) and is **never logged**.
- **The seam goes async:** `ResolveToken(string)` → `ValueTask<string> ResolveTokenAsync(string,
  CancellationToken)`. Token minting is I/O; sync-over-async would be deadlock-prone. Touches
  only Infrastructure (interface, `ConfiguredGitTokenProvider`, the two platform clients) and
  test fakes — **Core untouched**.
- **Real verdict:** `IGitPlatform.PostReviewAsync` gains the — already Core-owned —
  `ReviewVerdict` parameter. GitHub maps it to `event` (`APPROVE` / `REQUEST_CHANGES`), GitLab to
  `POST …/merge_requests/:iid/approve` (or `unapprove`, tolerating 404 = "was never approved").
  Only Core types cross the Core seam ⇒ Core rule intact.
- **Verdict posting is opt-in via `PostVerdict`** (`Naudit:GitHub:PostVerdict` /
  `Naudit:GitLab:PostVerdict`, bool, default **false**) — deliberately **orthogonal to `Auth`**
  (this refines the vault design note, which coupled it to `Auth=App`). Reasons: it also works
  with a service-account PAT or GitLab bot token (not only the App), and both platforms keep
  exactly today's behaviour until someone consciously opts in. Without opt-in GitHub stays at
  `event = "COMMENT"` and GitLab posts no approval call.
- **Verdict idempotency for free:** GitHub keeps only the **latest** review state per reviewer —
  a re-run with a new `event` supersedes the previous one (no stacking). Inline-comment de-dup
  stays a separate board item.
- **Webhook unchanged:** GitHub App webhooks use the same `X-Hub-Signature-256` HMAC scheme the
  endpoint already verifies (fail-closed). Only the `pull_request` event is needed. The
  installation token also works as the clone credential (`x-access-token:…`) — the SAST checkout
  path is unchanged.
- **Setup as simple as possible:** app creation via the **App Manifest flow** (GitHub creates the
  app with prefilled permissions + webhook and returns `AppId`/private key/webhook secret).
  Minimal permissions: `pull_requests: write`, `contents: read`, `metadata: read`. Three secrets
  once per deployment (Coolify), then per environment it is only "Install app".

## GitLab analogue (no App concept there)

- **Group/project access token** → auto bot user (`project_…_bot`), own identity, no human seat,
  plain token config ⇒ **0 code** (Naudit already applies tokens per request).
- **One group-level webhook** covers all projects of the group.
- Real review = the `approve`/`unapprove` call behind the same `PostVerdict` flag.
- ⚠️ Tier caveat: service accounts / access tokens are partially Premium on GitLab.com
  (self-managed Free is fine).

## Scope

- `IGitTokenProvider` → async (Infrastructure + fakes only).
- New `GitHubAppTokenProvider`, `GitHubAppOptions`, `GitHubAuthKind`; DI switch in
  `AddNauditInfrastructure` (GitHub branch) with fail-fast validation.
- `IGitPlatform.PostReviewAsync(…, ReviewVerdict verdict, …)`; GitHub `event` mapping and GitLab
  approve/unapprove behind `PostVerdict`; `ReviewService` passes the derived verdict.
- Docs: new `docs/github-app.md` (manifest + install guide), `docs/configuration.md`,
  `docs/platform-setup.md`, `CLAUDE.md`, appsettings structure hints.

Out of scope: publishing the app on the GitHub Marketplace (a private app is enough),
inline-comment idempotency/de-dup (separate board item), a Vault/Key-Vault-backed token provider
(the seam is ready; it would be just another `IGitTokenProvider` implementation).

## Behaviour change

None by default. With `Auth=App` comments/reviews appear as `Naudit[bot]`; with
`PostVerdict=true` Naudit sets a **blocking** review state (`REQUEST_CHANGES`) or an approval —
previously never. GitHub rejects `APPROVE`/`REQUEST_CHANGES` from the PR author (422), so
`PostVerdict=true` with the repo owner's PAT will fail on the owner's own PRs — documented; use
the App (or a service account) for that.

## References

- Vault design note: `BenediktsMind/1. Projects/Naudit/2026-07-02 Bot-Identität via GitHub App – Design.md`
- Plan: `docs/superpowers/plans/2026-07-02-github-app-auth.md`
- Prior art: `docs/superpowers/plans/2026-07-01-per-project-git-tokens.md` (the seam this builds on),
  `docs/superpowers/specs/2026-06-22-inline-comments-design.md` (Reviews API, `event=COMMENT` decision — consciously revised here)
- Docs (new): `docs/github-app.md`
