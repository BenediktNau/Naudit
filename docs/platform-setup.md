# Platform setup

Naudit is configured for **one** Git platform per deployment
(`Naudit:Git:Platform`, default `GitLab`); only that platform's webhook endpoint is
mapped. The keys are described in [Configuration](configuration.md).

## Set up the GitLab webhook (productive)

In real operation GitLab delivers the event itself:

1. Make the bot publicly reachable — for local tests via a tunnel:
   ```bash
   ngrok http 5080   # note the public URL: https://<id>.ngrok.io
   ```
2. In the target project → **Settings → Webhooks**:
   - **URL:** `https://<id>.ngrok.io/webhook/gitlab`
   - **Secret Token:** the same value as `Naudit:GitLab:WebhookSecret`
   - **Trigger:** only **Merge request events**
3. *Add webhook*, then *Test → Merge request events* (or open a real MR).

## Set up GitHub (productive)

### 1. Switch the Git platform to GitHub

Only the **configuration** changes, no code. Naudit then activates only the GitHub endpoint (`/webhook/github`):

```bash
dotnet user-secrets set "Naudit:Git:Platform"          "GitHub"             --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitHub:Token"          "<fine-grained-PAT>" --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitHub:WebhookSecret"  "<random-secret>"    --project src/Naudit.Web
```

### 2. Create a fine-grained PAT

In your GitHub account: **Settings → Developer settings → Personal access tokens → Fine-grained tokens → Generate new token**.

Required permissions:

| Permission | Scope | Reason |
| --- | --- | --- |
| **Pull requests** | Read and write | Read PR files, post the summary as an issue comment |
| **Contents** | Read | Repo access (required for the API calls) |

Store the token under `Naudit:GitHub:Token` (user-secrets, never `appsettings.json`).

> **Per-project tokens:** to scope a fine-grained PAT to a single repository (instead
> of one shared token for all), keep this global token as a fallback and add a
> per-repo override — see [Per-project tokens](configuration.md#per-project-tokens).

### 3. Set up the webhook in each target repository

Naudit reviews every repo that has a webhook configured and that the PAT can access.

1. In the target repo → **Settings → Webhooks → Add webhook**:
   - **Payload URL:** `https://<your-host>/webhook/github`
   - **Content type:** `application/json`
   - **Secret:** the same value as `Naudit:GitHub:WebhookSecret`
   - **Events:** enable only **Pull requests**
2. *Add webhook*. GitHub sends a ping — the bot answers `200`.

Naudit verifies the `X-Hub-Signature-256` signature (HMAC-SHA256 over the raw body) **fail-closed**: an empty or missing secret rejects every request with `401`.

Reviewed PR actions: `opened`, `reopened`, `synchronize` (new commits).

**Known limitation:** only the **first 100 changed files** of a PR are reviewed (`per_page=100` on the GitHub API call). Very large PRs (> 100 files) are truncated without notice.

## Simulate a review locally

The fastest way to exercise the **full** pipeline — with a local Ollama and a
simulated webhook event (no ngrok / GitLab webhook configuration). The bot still
fetches the real MR diff and posts a real comment.

> **Tip:** use a **throwaway project with a dummy MR** in your GitLab so you don't
> spam real MRs. You need its **project ID** (Settings → General) and the **MR IID**
> (the `!number`).

**1. Provide a local LLM:**

```bash
ollama pull llama3.1
ollama serve            # runs on http://localhost:11434
```

**2. Set secrets** (GitLab + a freely chosen webhook secret):

```bash
dotnet user-secrets set "Naudit:GitLab:BaseUrl"       "https://YOUR-GITLAB-INSTANCE" --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:Token"         "TOKEN_WITH_API_SCOPE"         --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:WebhookSecret" "test-secret"                  --project src/Naudit.Web
```

**3. Start the service:**

```bash
dotnet run --project src/Naudit.Web --urls http://localhost:5080
curl http://localhost:5080/health        # -> "healthy"
```

**4. Simulate the webhook event** (`id` = project ID, `iid` = MR number):

```bash
curl -X POST http://localhost:5080/webhook/gitlab \
  -H "X-Gitlab-Token: test-secret" \
  -H "Content-Type: application/json" \
  -d '{
        "object_kind": "merge_request",
        "project": { "id": 1234 },
        "object_attributes": { "iid": 5, "title": "Add retry to HTTP client", "action": "open" }
      }'
```

The endpoint answers **`200` immediately**; the review runs asynchronously. After a
few seconds a comment appears on MR `!5`, for example:

```markdown
**Summary:** The retry logic is sensible but has two weaknesses.

- `HttpClient` is created per call → socket exhaustion. Use a shared
  `HttpClient`/`IHttpClientFactory` instead.
- The retry loop catches every `Exception` and thereby swallows
  `OperationCanceledException`; the cancellation token should be propagated.
- No backoff between attempts — under load spikes this makes the problem worse.
```

**Troubleshooting:** because it's asynchronous, the result is not in the curl response.
If no comment appears, check the service logs (`Review failed for MR 5`) — typical
causes: wrong GitLab token (401), wrong project/MR ID (404), Ollama unreachable.
