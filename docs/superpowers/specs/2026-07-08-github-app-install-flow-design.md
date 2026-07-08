# GitHub App installation in the sign-in flow — Design

*2026-07-08 · Naudit*

## Problem

Signing in to the WebUI and getting Naudit onto your repositories are two completely separate
journeys today. A user who self-services via "Sign in with GitHub" lands in the dashboard
(pending approval), but Naudit still has zero access to their repos — they must independently
discover the GitHub App, navigate to its GitHub page, and install it there. Nothing in the
product connects the two steps.

## Goal

After signing in, the WebUI actively leads the user to install the Naudit GitHub App on their
account/repos: a banner ("Naudit is not installed on your GitHub repos yet → Install") that
knows the real installation state, opens GitHub's install page, and disappears once the app is
installed. Login-first, then guided install — one continuous onboarding flow without touching
the OAuth middleware.

## Key decisions

- **Login first, then install (not install-as-signup).** The flow starts with the existing
  GitHub OAuth login; a banner then guides to GitHub's install page. The alternative — starting
  on GitHub's install page with "Request user authorization (OAuth) during installation" — would
  deliver a one-click signup but redirects the OAuth `code` back **without** the ASP.NET
  correlation `state`, forcing a hand-rolled callback endpoint beside the OAuth middleware.
  Deliberately rejected: more code, more security surface, for a marginal UX gain.
- **Live installation check via the App JWT, no stored state.** A new
  `GitHubAppInstallationChecker` (`src/Naudit.Infrastructure/Git/GitHub/`, next to
  `GitHubAppTokenProvider`, reusing its RS256 JWT minting) asks GitHub per linked login:
  `GET /users/{login}/installation`, on 404 falling back to `GET /orgs/{login}/installation`
  (links may name an org) — 200 = installed. Always correct, detects uninstalls too, no DB
  column that can lie. Results are cached a few minutes (`IMemoryCache`) so dashboard reloads
  don't hammer the GitHub API.
- **No new config.** The install URL is `https://github.com/apps/{slug}/installations/new`;
  the app **slug** is fetched once from `GET /app` (App JWT) and cached for the process
  lifetime. Everything else (AppId, PrivateKey) already exists under `Naudit:GitHub:App`.
- **Feature gating, existing pattern.** The endpoint and checker are registered only when
  `Naudit:Git:Platform=GitHub` **and** `Naudit:GitHub:Auth=App` **and** the UI is on — same
  "not enabled ⇒ route not mapped ⇒ 404" pattern as the opt-in auth routes. On PAT or GitLab
  deployments the SPA never sees the feature (the status endpoint 404s; the banner stays
  hidden).
- **One new endpoint:** `GET /api/me/github-app` (authenticated). Resolves the current
  account's GitHub links and returns
  `{ installUrl, accounts: [{ login, installed: true | false | null }] }`.
  `installed: null` = check failed (GitHub unreachable, rate limit) — **fail-quiet**: log a
  warning, hide the banner, never surface an error. Accounts without any GitHub link get an
  empty `accounts` array (nothing to check, no banner). The endpoint accepts **pending**
  accounts (like `/api/me`) — the PendingPage banner depends on it.
- **Frontend: banner on Dashboard and PendingPage.** The pending moment is the prime
  onboarding slot ("while you wait for approval, install the app"). Banner shows iff at least
  one linked login has `installed === false`; its button opens `installUrl` (new tab).
  ProfilePage gets a small status row ("GitHub App installed ✓" / "Install" + a "Manage on
  GitHub" link).
- **Return trip is pure configuration.** The GitHub App's **Setup URL** is set to the Naudit
  host root (`https://<host>/`) — after installing, GitHub redirects the user back to the
  dashboard, the live check now reports installed, the banner is gone. No callback code.
  ("Request user authorization (OAuth) during installation" stays **off**; with it on, GitHub
  would redirect to the OAuth callback instead of the Setup URL.)
- **Docs: the GitHub App can replace the separate OAuth app for WebUI login.** A GitHub App
  has its own OAuth client id/secret usable with the exact same endpoints the WebUI login
  already uses — putting them into `Naudit:Ui:Auth:GitHub:ClientId/ClientSecret` and adding
  the callback URL `https://<host>/auth/callback/github` to the app makes the separate OAuth
  app redundant. Documented as the recommended setup (one GitHub entity instead of two);
  zero code change.

## Scope

- New `GitHubAppInstallationChecker` in `src/Naudit.Infrastructure/Git/GitHub/` (slug fetch +
  per-login installation check + caching), registered in the GitHub+App branch of
  `AddNauditInfrastructure`.
- New `GET /api/me/github-app` endpoint in `src/Naudit.Web/Endpoints/` (mapped only when
  platform=GitHub, Auth=App, UI on).
- SPA: install banner (Dashboard + PendingPage), status row on ProfilePage, query hook + API
  type.
- Docs: `docs/github-app.md` (Setup URL, install-from-the-UI flow, app-as-OAuth-provider
  setup), `docs/webui.md` (banner behaviour), `CLAUDE.md`.

Out of scope: storing installation ids, webhook-driven installation tracking
(`installation` events), install-as-signup (rejected above), any GitLab analogue (GitLab has
no app/installation concept; group webhooks already cover it).

## Behaviour change

None for existing deployments by default: the endpoint and banner only exist on
GitHub+App+UI deployments. There, signed-in users with GitHub links see the install banner
until the app is installed on their account/org — no change to reviews, webhooks, or the
access gate.

## Testing

- `GitHubAppInstallationChecker` unit tests via `StubHttpMessageHandler`: user-installation
  200/404→org 200/404, API error ⇒ `null`, slug fetch, cache hit (no second HTTP call).
- Endpoint tests via `WebApplicationFactory`: 401 unauthenticated; feature off ⇒ 404;
  happy path shape with a stubbed checker.
- Frontend: `npm run lint && npm run build` (no test setup in the SPA, as before).

## References

- Prior art: `docs/superpowers/specs/2026-07-02-github-app-auth-design.md` (App JWT/token
  provider this builds on), `docs/superpowers/specs/2026-07-07-webui-design.md` (BFF auth,
  pending flow, GitHub links).
- GitHub docs: [Get a user installation for the authenticated app](https://docs.github.com/en/rest/apps/apps#get-a-user-installation-for-the-authenticated-app),
  [Get an organization installation](https://docs.github.com/en/rest/apps/apps#get-an-organization-installation-for-the-authenticated-app),
  [Get the authenticated app](https://docs.github.com/en/rest/apps/apps#get-the-authenticated-app) (`slug`).
