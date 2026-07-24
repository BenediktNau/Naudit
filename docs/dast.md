# DAST — dynamic testing of the PR's running app

> **Status: not yet released.** DAST is in development on `feat/dast-app-runner`
> (design: `docs/superpowers/specs/2026-07-19-dast-design.md`); it ships in two PRs
> (PR 1 app-runner, PR 2 probing analyzer). The `Naudit:Review:Dast:*` keys below do
> not exist in any current release and may still change — **part B of this guide only
> works once both PRs are merged.** Part A can (and should) be prepared today; it is
> the same prerequisite the released [session sandbox](session-sandbox.md) uses. This
> page will become the feature reference when PR 1 lands.

## What it does

During a review, Naudit builds the **PR's own `Dockerfile`**, starts the app as a
sibling container in an internet-less review network, and lets the LLM probe it through
a Playwright browser (via MCP). The findings feed the review prompt as grounding —
exactly like Semgrep/Trivy. They never decide the merge gate; the verdict stays
LLM-driven ([review gate](review-gate.md)).

All reachability runs through the Docker socket: a passive probe container inside the
review network executes the healthcheck (and later speaks MCP) via `docker exec` — no
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

## Part B — enable DAST (once released; keys reflect the current design)

Prerequisites:

- **`Naudit:AccessGate:Mode=Registered` is strongly recommended.** DAST executes code
  from pull requests; it is meant for repositories you trust. Do not enable it in
  `Open` mode.
- The target repo needs a `Dockerfile` (path configurable) starting a single web
  service that serves HTTP on a fixed port.
- The manual MCP end-to-end gate from the [MCP tool-loop](mcp-tools.md) must be done.

Scalar keys are DB-managed (Settings page, then restart) or set as environment
overrides:

```bash
Naudit__Review__Dast__Enabled=true          # kill switch (default false)
Naudit__Review__Dast__DockerfilePath=Dockerfile
Naudit__Review__Dast__AppPort=8080          # port the app listens on inside its container
Naudit__Review__Dast__HealthPath=/          # HTTP path for the healthcheck
Naudit__Review__Dast__TimeBudget=00:05:00   # caps build + start + probe together
Naudit__Review__Dast__MemoryLimitMb=1024
```

List-shaped keys are env-only (indexed syntax):

```bash
# "dast" joins the analyzer list:
Naudit__Sast__Analyzers__0=opengrep
Naudit__Sast__Analyzers__1=dast

# Per-project allowlist — EMPTY means NO project runs through DAST:
Naudit__Review__Dast__Projects__0=owner/repo
```

Then redeploy (env change) or restart via the Settings page (DB change), open a test PR
in an allow-listed project, and watch the logs: image build → `naudit-dast-*`
containers/network on the host → probing → guaranteed teardown.

## Failure behaviour = fail-open

No Dockerfile, broken build, healthcheck timeout, missing socket, exceeded time
budget — each logs a warning and yields empty DAST findings. **A review never fails
because of DAST.** Leftovers from a crash (`naudit-dast-*`) are removed by an orphan
sweeper at startup.
