# DAST — dynamic testing of the PR's running app

Naudit builds the pull request's **own `Dockerfile`**, starts it as an isolated sibling
container, and — from PR 2 on — probes the running app with an LLM driving a headless
browser (Playwright, via MCP). Findings feed the review prompt as grounding, exactly
like Semgrep/Trivy; they never decide the merge gate, which stays LLM-driven
([review gate](review-gate.md)).

**PR 1 (this doc's current state) ships the app runner only: it builds the image, starts
the app plus a passive probe container, and health-checks it — nothing calls it yet, and
no findings are produced.** The `Naudit:Sast:Analyzers` list does **not** have a `"dast"`
entry yet — that, `DastAnalyzer`, and the actual browser probing arrive in PR 2. The
config keys below already work; enabling them today spins up and tears down an isolated
container per review with no visible effect on findings.

All reachability runs through the Docker socket: a passive probe container inside the
review network executes the healthcheck (and, in PR 2, speaks MCP) via `docker exec` — no
port is published anywhere, and Naudit never joins the network itself. Read
[the Docker socket](docker-socket.md) first for what socket access implies.

## Part A — prepare the host today

1. **Find the host's `docker` group GID** (SSH to the Docker/Coolify host):

   ```bash
   stat -c '%g' /var/run/docker.sock     # e.g. 984
   ```

2. **Mount the socket + add the group** — see
   [the Docker socket › Setup](docker-socket.md#setup) for the Compose snippet and the
   Coolify specifics (Compose resource recommended; bare-metal works too).

3. **Verify** in the container terminal: `ls -l /var/run/docker.sock` and `id` (the GID
   from step 1 must be listed). Optionally smoke-test the shared seam by enabling the
   [session sandbox](session-sandbox.md) once.

## Two switches

DAST builds and **executes** code from a pull request, so it is gated twice — both must
agree, because this is a different risk class than the session sandbox (foreign PR code,
not Naudit's own subscription containers):

- **`Naudit:Review:Dast:Enabled`** — the global kill switch (default `false`).
- **`Naudit:Review:Dast:Projects`** — an allowlist of `owner/repo` (GitHub) or the GitLab
  project id, **empty by default, which means no project runs through DAST** even with
  `Enabled=true`.

This belongs on repositories you trust. **Do not enable it with
`Naudit:AccessGate:Mode=Open`** — pair it with `Registered` so only projects of active,
vetted accounts can trigger a build-and-run of their own code.

## Topology

```text
Docker network  naudit-dast-net-<key>   (internal: true → no egress)
 ├─ app container    naudit-dast-app-<key>   (built from the PR's Dockerfile)
 └─ probe container  naudit-dast-pw-<key>    (ProbeImage; healthcheck via docker exec — PR 2 turns it into the Playwright-MCP server)
No published ports anywhere and Naudit never joins the network — every interaction is a
`docker exec` through the socket, so the app is unreachable from the host and the internet.
```

`<key>` is a random per-run suffix; the image, network, app container and probe container
of one review all share it (`naudit-dast-img-<key>`, `naudit-dast-net-<key>`,
`naudit-dast-app-<key>`, `naudit-dast-pw-<key>`).

## Config

All keys live under `Naudit:Review:Dast:*`. The scalars are DB-managed (Settings page,
then restart) or settable as environment overrides, **except `HealthPollInterval`** which is env/appsettings-only;
`Projects` is list-shaped and therefore **env/appsettings-only** (indexed syntax), like `ProjectTokens`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Global kill switch. |
| `Projects` | *(empty)* | Allowlisted `owner/repo` / GitLab project ids. Empty = no project runs. Env-only. |
| `DockerfilePath` | `Dockerfile` | Path to the PR's Dockerfile, relative to the checkout root. Missing ⇒ DAST is skipped for that PR. |
| `AppPort` | `8080` | Port the app listens on inside its own container. |
| `HealthPath` | `/` | HTTP path used for the healthcheck. |
| `TimeBudget` | `00:05:00` | Caps build + start + healthcheck together; expiry ⇒ no dynamic grounding. |
| `HealthPollInterval` | `00:00:01` | Delay between two healthcheck attempts while waiting for the app to come up. — env/appsettings-only, not on the Settings page. |
| `MemoryLimitMb` | `1024` | Memory limit applied to both the app and the probe container. |
| `CpuLimit` | `1.0` | CPU limit (NanoCPUs-equivalent) applied to both containers. |
| `PidsLimit` | `256` | PID limit applied to both containers. |
| `MaxContextMb` | `200` | Cap on the tar'd build context (it travels through the socket into the daemon); over the cap ⇒ build is skipped. |
| `DockerSocketPath` | `/var/run/docker.sock` | Engine socket path. Only takes effect if the session sandbox isn't already active — see [Docker socket sharing](#docker-socket-sharing) below. |
| `ProbeImage` | `mcr.microsoft.com/playwright/mcp:latest` | Image for the probe container. Pulled on demand and deliberately **not** `naudit-dast-`-prefixed, so it survives as a cache across reviews (never removed by the runner or the orphan sweeper). |

### Docker socket sharing

`DastOptions` and the session sandbox each carry their own `DockerSocketPath`, but only
**one** `IDockerClient` is registered for the whole process: if
`Naudit:Ai:SessionSandbox=Docker`, its socket path wins over `DastOptions.DockerSocketPath`
even when both features are enabled. In practice both mount the same host socket, so this
only matters if you ever point the two at genuinely different sockets.

## Isolation

Per review, both the app and the probe container get:

- a dedicated **`internal: true`** Docker network (`naudit-dast-net-<key>`) — no egress,
  no route to the internet or any other Naudit-managed network;
- **no published ports** anywhere — the app is unreachable from the host, let alone the
  internet;
- `MemoryLimitMb` / `CpuLimit` / `PidsLimit` resource limits;
- `--cap-drop ALL` and `no-new-privileges`;
- **no volume**, **no environment variables**, and no Naudit secrets of any kind;
- no access to the Docker socket itself — only Naudit's own process talks to the engine,
  never a container it started.

Naudit itself never joins the review network; every interaction (healthcheck today,
MCP-over-exec in PR 2) is a `docker exec` into the probe container from the host side of
the socket.

## Fail-open behaviour

Every failure path ends in teardown and `null` (no dynamic grounding) — a review never
fails because of DAST:

| Condition | Result |
| --- | --- |
| Project not in `Projects` (or `Enabled=false`) | Skipped before any Docker call. |
| No `Dockerfile` at `DockerfilePath` in the checkout | Skipped, logged at `Information`. |
| Build context over `MaxContextMb` | Skipped, logged at `Warning`. |
| Image build fails | Teardown, skipped, logged at `Information` (with the build log). |
| App never becomes healthy within `TimeBudget` | Teardown, skipped, logged at `Information`. |
| Docker socket/engine unreachable, or any other unexpected error | Teardown, skipped, logged at `Warning`. |
| `TimeBudget` exceeded | Teardown, skipped (end state identical to "never healthy", but log line differs by phase: expiry during health poll → "unreachable" info line; expiry during build/start → generic warning catch). |
| Caller cancellation (the review itself is being cancelled) | Teardown, then the cancellation is **rethrown** — the only path that does not swallow the failure. |

## Lifecycle & teardown

`IAppRunner.RunAsync` returns a `RunningApp?` whose `DisposeAsync` is idempotent and tears
down, in order: probe container → app container → network → built image (best-effort,
each step independently swallowed and logged). The `ProbeImage` itself is **never**
removed — it is a deliberate cache shared across reviews.

Because a crash or `kill -9` mid-review can leave containers/networks/images behind
before `DisposeAsync` runs, `DastOrphanSweeper` (an `IHostedService`, registered only when
`Enabled=true`) removes every `naudit-dast-*` container, network and image at startup —
prefix-matched only, so unrelated Docker resources on the host are never touched. It is
fail-quiet: a missing or broken socket at startup just logs a warning and lets the host
come up.

## Requirement

A reachable Docker engine socket on the same host. Naudit's Docker client speaks Unix
sockets only, so this is **Linux-only** — the `win-x64` release binary cannot use DAST.
It does not matter whether Naudit itself runs **containerized** (socket mounted +
`group_add`) or as a **bare process** (user in the `docker` group) — all interaction with
the review network runs through the socket either way, so both deployment forms behave
identically. See [the Docker socket](docker-socket.md) for the trust implications of
socket access and the setup steps for both deployment forms — the same note applies here
verbatim.
