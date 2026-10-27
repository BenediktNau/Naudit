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
| `Naudit:Ai:SessionRouting` | `Single` | Set to `Author` to enable author-session routing (`Single` \| `Author`; a legacy `RoundRobin` is treated as `Author`). |
| `Naudit:Ai:AuthorSessions:Model` | `sonnet` | CLI model (alias or full id) for author-session runs — independent of `Naudit:Ai:Model`. |
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
  local accounts (Naudit cannot verify an external identity against a local account). A
  login can be claimed by **one account only** (case-insensitive): `PUT /api/me/claude-session`
  answers `409 Conflict` when another account already holds it, so nobody can redirect an
  existing user's MRs to their own subscription. If old data still contains duplicates, the
  router treats the login as ambiguous — no author session, the review runs on the global
  client, and a warning is logged (clean up the duplicate on the profile page). The
  remaining worst case is self-inflicted — a user who claims a *free* login they do not own
  only spends **their own** subscription on someone else's MRs; a token is never used
  against its owner's wishes and never leaks.
- **Storage:** tokens are encrypted at rest with ASP.NET Data Protection (purpose
  `Naudit.AiSessions`) and are write-only — the API never returns them.
- **Isolation:** every CLI run gets its own `CLAUDE_CONFIG_DIR`; parallel runs with
  different tokens never share state. The cooldown registry is in-memory by design — a
  restart merely allows one extra retry.

## Requirements

The `claude` CLI ships in the container image since this feature (pinned native binary,
checksum-verified). For bare-metal hosts, install it as described in
`docs/claudecode-provider.md`.

## Routing modes

`Naudit:Ai:SessionRouting` selects how the chat client is chosen per review:

- `Single` (default) — the global provider (`Naudit:Ai:*`), today's behaviour.
- `Author` — the PR author's own subscription (the author-session flow above).

### Round-robin pooling was removed

Earlier versions had a third mode, `RoundRobin`, that rotated an opt-in pool of
subscriptions across *all* reviews, regardless of authorship. It has been removed: no
user's session is ever used for someone else's review. The profile's pool opt-in is gone
and the `Accounts.ShareSessionInPool` column is dropped by the `RemoveSharePoolFlag`
migration.

A leftover `Naudit:Ai:SessionRouting=RoundRobin` (env var or Settings DB row) does **not**
crash the host or put it into recovery mode: it is treated as `Author` and a startup
warning (log + Settings page) asks you to switch to `Author` or `Single`. The Settings API
no longer accepts `RoundRobin` as a new value.

## Runtime isolation

By default, `Author` runs execute the `claude` CLI as an
in-process subprocess with a fresh, empty `CLAUDE_CONFIG_DIR` per review — a cold
re-auth every time. With `Naudit:Ai:SessionSandbox=Docker` each account's CLI instead
runs in its own long-lived sibling container over the host's Docker socket, keeping
its authenticated session warm between reviews and isolated from every other account's
container. This is a separate, opt-in switch (default `None` = today's behaviour,
unaffected by anything above) — see [Session sandbox](session-sandbox.md) for config,
lifecycle, and the security trade-offs of mounting the Docker socket.
