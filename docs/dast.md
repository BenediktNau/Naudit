# DAST — dynamic testing of the PR's running app

Naudit builds the pull request's **own `Dockerfile`**, starts it as an isolated sibling
container, and probes the running app with an LLM driving a headless browser
(Playwright, via MCP). Findings feed the review prompt as grounding, exactly like
Semgrep/Trivy; they never decide the merge gate, which stays LLM-driven
([review gate](review-gate.md)).

`"dast"` is enabled purely by `Naudit:Review:Dast:Enabled` **and** the `Projects`
allowlist — `DastAnalyzer : ISastAnalyzer` self-registers in DI when `Enabled=true` (see
[`DependencyInjection.cs`](../src/Naudit.Infrastructure/DependencyInjection.cs)); it is
**not** an entry in the `Naudit:Sast:Analyzers` list and does not depend on
`Naudit:Sast:Enabled` — DAST and static analysis are independent switches. The config
keys below work end-to-end against the Docker engine (see
[Part A](#part-a--prepare-the-host-today)).

All reachability runs through the Docker socket: a passive probe container inside the
review network executes the healthcheck, then speaks MCP over the same `docker exec`
channel — no port is published anywhere, and Naudit never joins the network itself. Read
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
 └─ probe container  naudit-dast-pw-<key>    (ProbeImage; healthcheck via docker exec, then hosts the Playwright-MCP server for the probe loop)
No published ports anywhere and Naudit never joins the network — every interaction is a
`docker exec` through the socket, so the app is unreachable from the host and the internet.
```

`<key>` is a random per-run suffix; the image, network, app container and probe container
of one review all share it (`naudit-dast-img-<key>`, `naudit-dast-net-<key>`,
`naudit-dast-app-<key>`, `naudit-dast-pw-<key>`).

## Probing

`DastAnalyzer : ISastAnalyzer` (`src/Naudit.Infrastructure/Dast/DastAnalyzer.cs`) is the
caller that turns the app runner above into an actual dynamic scan:

1. `IAppRunner.RunAsync` builds and starts the PR's app (see [Topology](#topology)) and
   returns the healthy `RunningApp`, or `null`/an exception if it never comes up — either
   way `DastAnalyzer` returns no findings and the review continues (see
   [Fail-open behaviour](#fail-open-behaviour)).
2. `DastProbeSession.StartAsync` starts the Playwright-MCP server as a **stdio** process
   inside the already-running probe container via a `docker exec` with `ProbeMcpArgv`
   (`node /app/cli.js --headless --browser chromium --no-sandbox` by default — no `--port`,
   so the server never listens on a socket). The exec's stdin/stdout are wired to the MCP
   SDK's `StreamClientTransport` over a hand-rolled **bidirectional** Engine-API exec
   stream (`DockerExecStream`/`SocketDockerClient.ExecStreamAsync` — raw stdin write, demuxed
   stdout read, one HTTP hijacked connection). This is the same "no `docker` CLI in the
   image, no new NuGet" pattern as the [session sandbox](session-sandbox.md), just duplex
   instead of one-shot. A `HandshakeTimeout` (default 10s) bounds the MCP handshake
   (`McpClient.CreateAsync` + `ListToolsAsync`) so a dead or unreachable probe process can't
   hang the review forever.
3. The discovered MCP tools (browser navigate/click/snapshot/etc.) are handed to the
   **global** `IChatClient` (never the author-session router — same rule as
   `DistillingReviewGuidelines`) via MEAI's `UseFunctionInvocation`, with
   `MaximumIterationsPerRequest` set to `MaxProbeSteps` (default 12). One
   `GetResponseAsync` call runs the whole bounded agentic tool-loop: the model is told the
   app's internal URL and asked to probe it and return `{"findings":[{"severity","endpoint","summary"}]}`
   as strict JSON (`ChatResponseFormat.Json`).
4. The JSON response is parsed into `ScanFinding(Category = FindingCategory.Dast)`
   entries (severity mapped `high`/`medium`/else→`low`); anything that isn't valid JSON,
   or has no findings, just yields an empty list — this is a grounding step, not a
   fail-closed scan.
5. `DastProbeSession` is torn down (MCP client, then the exec stream) in a `finally`, and
   the app/probe containers, network and built image are torn down by `RunningApp`'s
   `DisposeAsync` regardless of outcome — teardown is guaranteed on every path, including
   exceptions.

Findings render in the prompt as a "DAST (dynamic)" grounding section
(`PromptBuilder.Build`), alongside SAST/SCA/secrets findings. **DAST findings never gate
the merge** — the verdict stays derived from the LLM's own findings via the severity-aware
gate ([review gate](review-gate.md)); DAST is purely additional grounding for the model.

Enablement: the analyzer self-registers as an `ISastAnalyzer` in DI whenever
`Naudit:Review:Dast:Enabled=true` — independent of `Naudit:Sast:Enabled` and **not** an
entry in `Naudit:Sast:Analyzers` (see the introduction above).

### Non-root app images only

An app image whose `Dockerfile` starts as **root and drops privileges internally** (the
stock `nginx` image is the canonical example) **fails to start** under this container's
`--cap-drop ALL` — dropping to an unprivileged user is itself a privileged operation the
sandbox denies. This is the isolation working as designed, not a bug: to be
DAST-probeable, an app's `Dockerfile` must already run as a non-root `USER` (no privilege
drop needed at container start). There is no config knob to relax this — loosening
`--cap-drop ALL` would undermine the isolation story for every other DAST run.

## Config

All keys live under `Naudit:Review:Dast:*`. Most scalars are DB-managed (Settings page,
then restart) or settable as environment overrides; `HealthPollInterval`, `ProbeMcpArgv`
and `HandshakeTimeout` are **env/appsettings-only**, not on the Settings page (see
`SettingsCatalog.cs` for the authoritative list); `Projects` is list-shaped and therefore
**env/appsettings-only** (indexed syntax), like `ProjectTokens`.

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
| `MaxProbeSteps` | `12` | Cap on the agentic probing loop (tool calls + model turns together, via `UseFunctionInvocation`'s `MaximumIterationsPerRequest`). Token-frugal by design — DAST is grounding, not an exhaustive scan. DB-managed. |
| `ProbeMcpArgv` | `["node", "/app/cli.js", "--headless", "--browser", "chromium", "--no-sandbox"]` | Argv that starts the Playwright-MCP server as a stdio process inside the probe container via `docker exec` (no `--port` ⇒ stdio, not HTTP). List-shaped ⇒ env/appsettings-only. |
| `HandshakeTimeout` | `00:00:10` | How long `DastProbeSession.StartAsync` waits for the MCP handshake (`McpClient.CreateAsync` + `ListToolsAsync`) before giving up — a backstop against a dead/unreachable probe process blocking the review forever. Env/appsettings-only. |

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

Naudit itself never joins the review network; every interaction (healthcheck, and the
Playwright-MCP probing loop) is a `docker exec` into the probe container from the host
side of the socket.

The spec's "read-only root filesystem where possible" hardening is deliberately **not**
implemented (many app images need a writable filesystem at startup).

## Residual risk: the build phase

The isolation story above — internal network, resource limits, `--cap-drop ALL` — applies
to the **running** app and probe containers. `docker build` runs **before** any of that:
`RUN` steps execute with the builder's default network (egress is available to whatever the
Dockerfile does) and without memory/CPU/PID caps, bounded only by the overall `TimeBudget`.
Image layers are written to the daemon's storage uncapped, and a build aborted by the
`TimeBudget` can leave dangling intermediate layers behind that the `naudit-dast-*` prefix
sweeper cannot match (they carry no such name). Run periodic `docker builder prune` /
`docker image prune` on DAST hosts to reclaim that. This is exactly why the `Projects`
allowlist is trusted-repos-only — DAST executes a PR's own build instructions with none of
the runtime isolation guarantees above.

## Residual risk: the probing phase

The probing loop hands the probe LLM page content from the running app — content the PR
author fully controls via the browser (page text, DOM, console/network output the MCP tools
surface). Its JSON summary flows into `PromptBuilder` as grounding for the *main* review
prompt, and from there can end up verbatim in a public PR comment: a bounded prompt-injection
channel from attacker-controllable page content through the probe LLM into the review output.
Mitigations: DAST findings never gate the merge (the verdict stays LLM-derived via the
severity/confidence gate, `docs/review-gate.md`); DAST findings pass through the same
redaction as SAST/SCA findings before the prompt; the probe container has no egress (see
Isolation above); and the DAST grounding section of the prompt now explicitly marks its
entries as unverified, content-derived observations to corroborate against the diff, not
established fact. This — together with the build-phase risk above — is why DAST stays
allowlist-gated (`Naudit:Review:Dast:Projects`) to repositories Naudit's operators trust.

## Fail-open behaviour

Every failure path ends in teardown and no dynamic grounding — a review never fails
because of DAST, with one exception: a caller cancellation (see the last row) also tears
down but then **rethrows**, so the review itself does stop, exactly as it would without
DAST in the picture. This covers both the app-runner phase (rows 1–7, `IAppRunner`) and
the probing phase that follows (rows 8–9, `DastAnalyzer`/`DastProbeSession`) — the
analyzer wraps the whole runner-then-probe sequence in one `try`/`catch`:

| Condition | Result |
| --- | --- |
| Project not in `Projects` (or `Enabled=false`) | Skipped before any Docker call. |
| No `Dockerfile` at `DockerfilePath` in the checkout | Skipped, logged at `Information`. |
| Build context over `MaxContextMb` | Skipped, logged at `Warning`. |
| Image build fails | Teardown, skipped, logged at `Information` (with the build log). |
| App never becomes healthy within `TimeBudget` | Teardown, skipped, logged at `Information`. |
| Docker socket/engine unreachable, or any other unexpected error | Teardown, skipped, logged at `Warning`. |
| `TimeBudget` exceeded | Teardown, skipped (end state identical to "never healthy", but log line differs by phase: expiry during health poll → "unreachable" info line; expiry during build/start → generic warning catch). |
| MCP handshake never completes within `HandshakeTimeout`, the probe process/exec stream errors, or the model's response isn't valid JSON | Teardown (session + app), no findings, logged at `Warning` (session/loop errors) or `Information` (non-JSON response). |
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

## Manual verification gate

CI never touches real Docker or a live model, so two things stay hand-verified:

1. **Duplex-exec round-trip:** `NAUDIT_TEST_DOCKER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj
   --filter SocketDockerClientTests` — exercises the bidirectional `docker exec` stream
   `DastProbeSession` relies on. Verified passing on Docker 29.5.3.
2. **Live probe (pre-prod gate):** a real target app + a real Playwright-MCP server + a
   model that actually supports the combination of a multi-step tool-loop *and* strict
   JSON output — this inherits the same MCP #54 gate already called out for the review
   tool provider ([MCP tools](mcp-tools.md)). Point `Naudit:Review:Dast:Projects` at a
   small web repo with a **non-root** `Dockerfile` (see
   [Non-root app images only](#non-root-app-images-only)), enable DAST, open a PR, and
   confirm: app + probe containers appear on `naudit-dast-net-*`, the MCP server answers
   `ListTools` over exec-stdio, the loop stays within `MaxProbeSteps`, any observations
   show up as a "DAST (dynamic)" grounding block in the review, and after the review no
   `naudit-dast-*` container/network/image remains.

Killing Naudit mid-probe and confirming `DastOrphanSweeper` clears the leftovers on
restart is worth a manual pass too, though it's exercised by the sweeper's own tests.

## Requirement

A reachable Docker engine socket on the same host. Naudit's Docker client speaks Unix
sockets only, so this is **Linux-only** — the `win-x64` release binary cannot use DAST.
It does not matter whether Naudit itself runs **containerized** (socket mounted +
`group_add`) or as a **bare process** (user in the `docker` group) — all interaction with
the review network runs through the socket either way, so both deployment forms behave
identically. See [the Docker socket](docker-socket.md) for the trust implications of
socket access and the setup steps for both deployment forms — the same note applies here
verbatim.
