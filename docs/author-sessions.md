# Author sessions (bring your own Claude subscription)

With author sessions enabled, each user can store the OAuth token of their own Claude
Pro/Max subscription (from `claude setup-token`) on their Naudit profile page. Reviews of
merge requests **authored by that user** then run through the Claude Code CLI with their
token instead of the globally configured AI provider. Everything else — MRs by users
without a token, bot MRs (Renovate/Dependabot), failing sessions — falls back to the
global provider. Usage is thereby distributed across the team: everyone carries the cost
of reviewing their own work.

## Why author-bound (and not a shared pool)

A round-robin pool over contributed subscriptions would mean one user's account does
another user's work — that is account sharing and violates the Anthropic consumer terms;
pooled accounts risk being suspended. The sanctioned pattern (compare Claude Code GitHub
Actions with your own `setup-token` token) is: **your own token automates your own work.**
Naudit therefore uses a stored token *only* for MRs the owning user authored — this
promise is part of the profile UI and of this document.

## Enabling author-mode routing

1. Settings → AI → **Session Routing** → set to `Author`, then restart via the banner.
2. Each participating user: profile page → **Claude session** → paste the token from
   `claude setup-token`, set the git login (auto-filled for GitHub accounts), **Test**.

| Key | Default | Meaning |
| --- | --- | --- |
| `Naudit:Ai:SessionRouting` | `Single` | Set to `Author` to enable author-session routing, or `RoundRobin` for the pool mode. |
| `Naudit:Ai:AuthorSessions:Model` | `sonnet` | CLI model (alias or full id) for author/round-robin runs — independent of `Naudit:Ai:Model`. |
| `Naudit:Ai:AuthorSessions:CooldownMinutes` | `30` | How long a failing session is skipped before it is tried again. |

## Behaviour

- **Routing:** MR author → active account with matching git login and stored token.
  GitHub: the author login comes from the webhook payload. GitLab: one extra API call
  resolves `author.username`. `POST /review` accepts an optional `authorLogin` field.
- **Fallback + retry:** any failure of an author run marks the session as cooling down
  and retries **once** on the global provider; if that fails too, the review fails closed
  (as any provider error does today). No review is lost to a rate-limited subscription.
- **Attribution:** the review audit stores which account's session carried a review
  (`AiSessionAccountId`), so the dashboard can show the distribution.
- **Trust model of the git login:** the MR author's login (the join key) is trustworthy —
  it comes from the HMAC-verified GitHub webhook, the GitLab API, or the shared-secret
  `POST /review`. The **account-side** `gitAuthorLogin` (which login an account claims), by
  contrast, is trusted as declared: it is auto-filled from the verified username for
  GitHub-OAuth accounts but can be overridden, and is fully self-asserted for GitLab/OIDC/
  local accounts (Naudit cannot verify an external identity against a local account). The
  worst case is self-inflicted — a user who claims a login they do not own only spends
  **their own** subscription on someone else's MRs; a token is never used against its
  owner's wishes and never leaks.
- **Storage:** tokens are encrypted at rest with ASP.NET Data Protection (purpose
  `Naudit.AiSessions`) and are write-only — the API never returns them.
- **Isolation:** every CLI run gets its own `CLAUDE_CONFIG_DIR`; parallel runs with
  different tokens never share state. The cooldown registry is in-memory by design — a
  restart merely allows one extra retry.

## Requirements

The `claude` CLI ships in the container image since this feature (pinned native binary,
checksum-verified). For bare-metal hosts, install it as described in
`docs/claudecode-provider.md`.

## Round-robin routing (shared pool)

`Naudit:Ai:SessionRouting` selects how the chat client is chosen per review:

- `Single` (default) — the global provider (`Naudit:Ai:*`), today's behaviour.
- `Author` — the PR author's own subscription (the author-session flow above).
- `RoundRobin` — rotate the **opt-in pool** of subscriptions across reviews, ignoring
  authorship. Reviews are processed sequentially, so this spreads usage over successive
  reviews (it does **not** run reviews in parallel).

**⚠️ Terms-of-service risk.** Round-robin uses one user's Claude subscription to review
another user's PR. Under Anthropic's consumer (Pro/Max) terms this is **account sharing**
and can get the pooled accounts suspended. It is opt-in on two levels: the operator sets
`SessionRouting=RoundRobin`, and each user must explicitly enable **"Add my session to the
round-robin pool"** on their profile (a token set for author-mode is *not* pooled without
that consent). Only active accounts with a token **and** that opt-in are rotated; accounts
on cooldown are skipped, and an empty pool falls back to the global provider. Failures fall
back to the global client with one retry, exactly as in author mode.
