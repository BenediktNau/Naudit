# Getting started

Naudit is a single container (`ghcr.io/benediktnau/naudit`) that reacts to merge-/
pull-request webhooks, has an LLM review the diff, and posts the result back as a
comment. Configuration lives in a database it manages itself — a **setup wizard** in
the WebUI configures it end to end on first start, no env-var studying required. An
env-var/user-secrets path remains for scripted or GitOps-style deployments.

## Quickstart: the setup wizard

```bash
docker run -p 8080:8080 -v naudit-data:/data ghcr.io/benediktnau/naudit
```

Open `http://localhost:8080` in a browser. With no configuration yet, Naudit serves the
**setup wizard** instead of the normal app — it walks through:

1. **Admin account** — create one (only possible while none exists yet — see the
   protection note below).
2. **Instance URL** — pre-filled from the request; used to build the webhook URLs shown
   at the end.
3. **Git platform** — GitHub (fine-grained PAT) or GitLab (base URL + API token), plus a
   generated webhook secret.
4. **AI provider** — Ollama, Anthropic, an OpenAI-compatible endpoint, or the Claude Code
   CLI, with a "Test connection" button (a failure here is only a warning — you can
   continue, e.g. if Ollama isn't reachable yet).
5. **Access model** — `Open` (every project with a valid webhook secret is reviewed) or
   `Registered` (only approved WebUI accounts).
6. **Review & apply** — a summary, then "Apply & restart": Naudit restarts itself
   in-process (a couple of seconds) and comes back configured, showing the webhook
   URL to paste into your platform.

> **Protection window:** step 1 only lets you create the admin account while **no**
> account exists yet (the same pattern Grafana/Portainer use) — after that, the wizard
> (and a later misconfiguration's recovery screen) requires that admin to log in. Don't
> expose the instance publicly until you've completed setup.

`-v naudit-data:/data` persists the SQLite database (settings, accounts, review
history) across restarts/upgrades — without it, setup runs again on the next container
start. See [WebUI › Setup wizard](webui.md#setup-wizard) for the full flow and
[Configuration › Setup mode](configuration.md#setup-mode) for exactly which keys the
wizard is filling in.

## Alternative: environment variables only

For scripted deployments (Compose, Kubernetes, a PaaS env panel), you can skip the wizard
entirely by setting the full required key set for your platform/provider **before** the
first start — Naudit then boots straight into normal operation:

```bash
docker run -d -p 8080:8080 -v naudit-data:/data \
  -e Naudit__Git__Platform=GitHub \
  -e Naudit__GitHub__Token=<fine-grained-PAT> \
  -e Naudit__GitHub__WebhookSecret=<random-secret> \
  -e Naudit__Ai__Provider=Anthropic \
  -e Naudit__Ai__Model=claude-sonnet-4-6 \
  -e Naudit__Ai__ApiKey=<anthropic-key> \
  ghcr.io/benediktnau/naudit:latest

curl http://localhost:8080/health   # -> healthy
```

`Naudit:Git:Platform=GitLab` needs `GitLab:BaseUrl` + `GitLab:Token` +
`GitLab:WebhookSecret` instead — see the required-key table in
[Configuration › Setup mode](configuration.md#setup-mode) (which keys are required
depends on the platform and AI provider you pick) and
[Configuration](configuration.md) for the full key reference, including the other AI
providers. Point the platform's webhook at `https://<your-host>/webhook/github` (or
`/webhook/gitlab`) — see [Platform setup](platform-setup.md) for the exact steps
(token scopes, webhook fields).

Everything not in the required set — access-gate mode, sign-in providers, review/gate
tuning — can either be set the same way or left for an admin to configure later on the
WebUI's Settings page (no wizard involved, since setup mode only triggers on a
*missing* required key).

## Next steps

- [Configuration](configuration.md) — every `Naudit:*` key, secrets, choosing an AI provider
- [WebUI](webui.md) — the setup wizard, dashboard, access gate, editable Settings page
- [Platform setup](platform-setup.md) — wiring up the GitLab/GitHub webhook manually
- [GitHub App setup](github-app.md) — bot identity, one-click install, real blocking reviews
- [Deployment](deployment.md) — the container image, environment-variable template, release pipeline
- [CI integration](ci-integration.md) — a synchronous `POST /review` as a merge gate
