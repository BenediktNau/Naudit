# GitHub App setup (bot identity)

Naudit can act on GitHub as a **GitHub App** instead of a personal access token (PAT). This is
the **recommended** way to run Naudit against GitHub in production; see
[Platform setup](platform-setup.md#set-up-github-productive) for the PAT-based dev/fallback path.

## Why

- **Bot identity.** Comments and reviews appear as `Naudit[bot]`, not as the user who owns a PAT.
- **One-click install.** Installing the app on a repo or an entire org is a single "Install app"
  click — no per-repo token to mint, no per-repo webhook to configure.
- **One central webhook.** The app has a single webhook URL for every installation; adding a repo
  never touches Naudit's configuration.
- **Short-lived tokens.** Installation tokens are minted on demand and expire after 1 hour — no
  long-lived PAT sitting in a secret store.
- **Real, blocking reviews.** With `Auth=App` a real review verdict (see
  [`PostVerdict`](configuration.md)) is actually possible. GitHub rejects `APPROVE`/
  `REQUEST_CHANGES` submitted by the **PR author** with HTTP 422 — with the repo owner's own PAT a
  real verdict is impossible on the owner's own PRs. A separate bot identity is the prerequisite,
  not just cosmetics. (If GitHub does reject a verdict with 422, Naudit degrades gracefully: it
  re-posts the same review as a plain `COMMENT` and logs a warning — only the verdict is lost,
  never the review content.)

## 1. Create the app (once per deployment)

Either via the UI or the manifest flow — both produce the same app.

**UI path:** **Settings → Developer settings → GitHub Apps → New GitHub App** (personal account)
or the equivalent org settings page.

**Manifest flow (faster):** GitHub can create the app for you from a prefilled manifest and hand
back the App ID, private key, and webhook secret in one step — see
[Creating a GitHub App from a manifest](https://docs.github.com/en/apps/sharing-github-apps/registering-a-github-app-from-a-manifest).

Either way, configure:

| Setting | Value |
| --- | --- |
| **Webhook URL** | `https://<your-host>/webhook/github` |
| **Webhook secret** | a random value — this becomes `Naudit:GitHub:WebhookSecret` |
| **Permissions → Pull requests** | Read and write |
| **Permissions → Contents** | Read-only |
| **Permissions → Metadata** | Read-only (mandatory default) |
| **Subscribe to events** | `Pull request` |
| **Setup URL** (optional) | `https://<your-host>/` — after "Install app", GitHub sends the user back to the Naudit dashboard; the install banner clears itself once the fresh state is read (cached a few minutes — see below) |

Then **generate a private key** for the app (app settings page, "Private keys" section) and
download the `.pem` file — this becomes `Naudit:GitHub:App:PrivateKey`.

Note the numeric **App ID** shown at the top of the app's settings page — this becomes
`Naudit:GitHub:App:AppId`.

## 2. Configure Naudit

```bash
dotnet user-secrets set "Naudit:GitHub:Auth"           "App"                --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitHub:App:AppId"       "123456"             --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitHub:App:PrivateKey"  "$(cat app-private-key.pem)" --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitHub:WebhookSecret"   "<random-secret>"    --project src/Naudit.Web
```

`App:PrivateKey` accepts either the **raw PEM text** or a **base64-encoded PEM** — base64 is
recommended for environment variables (Coolify/Docker), since a raw multi-line PEM is awkward to
carry through `KEY=VALUE` env syntax:

```bash
base64 -w0 app-private-key.pem   # -> one line, safe for an env var
```

`Naudit:GitHub:App:InstallationId` is optional: if the app is installed on a single, fixed
installation, setting it skips the per-repo installation lookup on every review. Leave it unset to
let Naudit resolve the installation per repo automatically.

Optionally enable a real, blocking review verdict (see [Configuration](configuration.md)):

```bash
dotnet user-secrets set "Naudit:GitHub:PostVerdict" "true" --project src/Naudit.Web
```

**Coolify / container (environment variables — `:` maps to `__`):**

```bash
Naudit__GitHub__Auth=App
Naudit__GitHub__App__AppId=123456
Naudit__GitHub__App__PrivateKey=<base64-encoded-PEM>   # 🔒 secret
Naudit__GitHub__WebhookSecret=<random-secret>          # 🔒 secret
Naudit__GitHub__PostVerdict=true                        # optional
```

With `Auth=App`, `Naudit:GitHub:Token` and `Naudit:GitHub:ProjectTokens` are **ignored** — every
API call and every checkout clone uses a freshly minted installation token instead.

## Install from the Naudit WebUI

Once `Naudit:GitHub:Auth=App` and the WebUI (`Naudit:Ui:Enabled=true`) are both on, a signed-in
user whose GitHub account/org does not yet have the app installed sees an **install banner** on
the dashboard (and on the pending screen, while they wait for admin approval). The banner links
straight to the app's install page; after installing, GitHub returns them to the Naudit dashboard
(the **Setup URL** above). Naudit re-checks the installation state against GitHub
(`GET /users/{login}/installation`, org fallback), so the banner clears once the fresh state is
read and a later uninstall makes it reappear. The result is cached in memory for a few minutes per
login (so dashboard reloads don't hammer the GitHub API) — a reload in the first minutes right
after installing may therefore still briefly show the banner. The Profile page shows the same
status per linked GitHub login.

This needs no extra configuration beyond `Auth=App`: Naudit derives the install link from the
app's own slug (`GET /app`).

## One app for both bot identity and WebUI login (recommended)

A GitHub App also carries an OAuth **client id/secret**, so the *same* app can power the WebUI
"Sign in with GitHub" — no separate OAuth App needed. In the app settings:

- Note the **Client ID** and generate a **client secret** (app settings → "Client secrets").
- Add the callback URL **`https://<your-host>/auth/callback/github`** (app settings →
  "Callback URL").
- Enable "Request user authorization (OAuth) during installation" **only if** you want the
  install page to double as sign-in; leave it off to keep the Setup-URL return trip described
  above.

Then point the WebUI login at those credentials:

```bash
dotnet user-secrets set "Naudit:Ui:Auth:GitHub:Enabled"      "true"        --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ui:Auth:GitHub:ClientId"     "<app-client-id>"     --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ui:Auth:GitHub:ClientSecret" "<app-client-secret>" --project src/Naudit.Web
```

See [WebUI](webui.md) for the login/approval flow.

## 3. Install the app

On the app's public page (or **Settings → Developer settings → GitHub Apps → <app> → Install
App**), click **Install**, then pick the repositories (or "All repositories") for a repo/org.
That's the entire per-repo integration step — no further webhook or token setup needed per repo.

If Naudit sees a PR for a repo where the app is not installed, it fails fast with an error that
names the repo (`GET .../installation` returns 404) — install the app there and retry.

## GitLab analogue

GitLab has no "app" concept, but the same bot-identity idea is achieved with plain configuration:

- **Group or project access token**, which GitLab attaches to an automatic bot user
  (`project_<n>_bot_<...>`) — no human seat consumed, and comments appear under that bot identity
  instead of a real person's account. Configure it exactly like any other token (see
  [Per-project tokens](configuration.md#per-project-tokens)) — Naudit needs no code change for
  this, it's zero extra integration work.
- **One group-level webhook** (**Group → Settings → Webhooks**) covers every project in the
  group, so adding a new project needs no webhook change — same one-central-webhook property as
  the GitHub App.
- Enable a real, blocking approval with `Naudit:GitLab:PostVerdict = true` (calls the MR
  `approve`/`unapprove` endpoint from the derived verdict).

> **Tier caveat:** group/project access tokens (and the service-account concept in general) are
> partially a **Premium** feature on GitLab.com; self-managed GitLab **Free** allows them. Check
> your plan before relying on this path.
