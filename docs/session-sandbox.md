# Session sandbox (containerised subscription sessions)

The [author-sessions](author-sessions.md) `Author` and `RoundRobin` routing modes run
each review's `claude` CLI as a fresh, short-lived subprocess: every review gets a
brand-new empty `CLAUDE_CONFIG_DIR`, so the CLI re-authenticates from the stored token
on every single run. That works, but it is a cold start every time and gives every
run the same host process namespace.

With `Naudit:Ai:SessionSandbox = Docker`, those subscription runs move into
**long-lived sibling containers, one per account**, started over the host's Docker
socket. Two effects:

- **Warm sessions.** The CLI process and its authenticated state persist between
  reviews (long-lived container + a named volume for `HOME`) — no cold re-auth per
  review.
- **Isolation.** Each account's CLI runs in its own container with its own volume; a
  run never sees another account's token or on-disk state.

This is **purely additive**. The default is `None`, which is exactly today's
in-process behaviour — nothing changes unless you opt in. It only applies to the
`Author`/`RoundRobin` session-routing modes ([`Naudit:Ai:SessionRouting`](author-sessions.md));
in `Single` mode (the global provider) it has no effect at all.

**Scope note.** This feature is about isolation and warm-session performance, **not**
about hiding round-robin pooling from Anthropic. It changes nothing about the
terms-of-service position of the `RoundRobin` mode described in
[Author sessions › Round-robin routing](author-sessions.md#round-robin-routing-shared-pool):
pooling one user's subscription across other users' reviews is still account sharing
under Anthropic's consumer terms, opt-in, and the operator's call to make. The sandbox
does not build any mechanism intended to evade detection of that.

## Config keys

All keys live under `Naudit:Ai:SessionSandbox`/`Naudit:Ai:Sandbox` and are DB-managed
like any other setting (Settings page → AI; none are secrets). Changing any of them
requires a restart via the "restart required" banner, same as other config changes.

| Key | Default | Meaning |
| --- | --- | --- |
| `Naudit:Ai:SessionSandbox` | `None` | `None` = today's in-process CLI runs. `Docker` = containerised sibling sessions (this feature). |
| `Naudit:Ai:Sandbox:IdleTimeout` | `2.00:00:00` (2 days) | How long a running account container may sit unused before the sweeper stops it (stop, not remove). |
| `Naudit:Ai:Sandbox:MaxLiveContainers` | `5` | Cap on simultaneously *running* session containers; the manager stops the least-recently-used one before starting one more (a floor of 1 is enforced). |
| `Naudit:Ai:Sandbox:DockerSocketPath` | `/var/run/docker.sock` | Path to the Docker engine socket. |
| `Naudit:Ai:Sandbox:Image` | *(empty)* | Optional image override. Empty means self-inspection: Naudit resolves its own image via `docker inspect $HOSTNAME`, so the `claude` CLI baked into the Naudit image is always used and never drifts from the running version. |

`IdleTimeout` is deliberately long: in day-to-day operation `MaxLiveContainers`
(LRU-stop before every new start) is the real resource bound, not the sweeper. The
sweeper is the safety net for "this account has been quiet for a couple of days"
(weekend, vacation), not for routine capacity control.

## Lifecycle

- **Container per account.** `SessionContainerManager` names each account's container
  `naudit-session-<accountId>`, running the **same Naudit image** with `sleep infinity`
  as its command (no second image to build or maintain).
- **Warm auth via a named volume.** A volume also named `naudit-session-<accountId>`
  is mounted at `/home/app` (the image's `HOME`) — that is where the `claude` CLI keeps
  its credentials, so authenticated state survives both a container **stop** and a
  Naudit **restart**.
- **Reviews run via `docker exec`.** Each review execs into the account's container
  instead of spawning a host subprocess. Standard input (the diff/prompt) is written
  to a file inside the container first and redirected in via a small shell wrapper
  (`sh -c 'exec "$0" "$@" < /tmp/naudit-stdin'`), so the original argv reaches `claude`
  unchanged with no shell-quoting risk.
- **Token-only exec environment.** Only `CLAUDE_CODE_OAUTH_TOKEN` is forwarded into the
  exec; the host-side `CLAUDE_CONFIG_DIR` is deliberately dropped so the container's own
  `HOME` (the volume) wins and the warm session is actually used.
- **Per-account lock.** A `SemaphoreSlim` per account serializes execs into the same
  container — two concurrent reviews for one account never race on the CLI's credential
  cache. This matches round-robin's sequential processing model.
- **LRU cap.** Before starting a container that would exceed `MaxLiveContainers`, the
  manager stops the running account container with the oldest `LastUsed` timestamp.
  Stopping, not removing — the warm session lives in the volume and comes back on the
  next `docker start`.
- **Idle sweeper.** A background service ticks every 5 minutes, stopping any running
  account container idle longer than `IdleTimeout` (stop, never remove).
- **Adoption after a Naudit restart.** On startup the sweeper lists existing
  `naudit-session-*` containers and adopts them as "just used" — Naudit's own
  config-change restart loop does not touch these sibling containers, so a restart
  never orphans a warm session.
- **Removal on token deletion / pool opt-out.** Deleting an account's stored Claude
  session token, or turning off "share my session in the round-robin pool", removes
  that account's container **and** its volume (best-effort — a Docker failure there
  never blocks the underlying DB change). This is deliberate: the volume holds live
  CLI credentials and must not outlive the account's consent to use them.
- **A running exec is never stopped out from under itself.** Both the idle sweeper and
  the LRU cap use a non-blocking lock attempt before stopping a container; if an exec
  is in flight for that account, the container is skipped that round rather than being
  killed mid-review (the LRU cap can then stay transiently over its configured limit by
  one container until the next pass).

## Fail-open matrix

A review must never fail *because of* the sandbox. Every Docker-facing failure mode
falls back to the existing in-process runner:

| Situation | Behaviour |
| --- | --- |
| Socket missing or unusable (`PingAsync` fails) | In-process fallback for every run; the sweeper re-pings every 5 minutes and switches back to sandboxed runs automatically once the socket is reachable again (self-healing, no restart needed). |
| Container start/exec fails with a Docker-plumbing error | One retry (re-`EnsureRunning` + exec again — covers "container was stopped/removed externally"); if that also fails, in-process fallback for that review. |
| Exec exceeds the review timeout | Same semantics as the in-process runner: the container is stopped (which kills the exec) and a `TimeoutException` is thrown — this is **not** swallowed as a fallback case, it is a real timeout. |
| `claude` itself exits non-zero | Not a sandbox failure at all — it's a normal CLI error and follows the regular error path, same as an in-process run. |

## Operations

- **`MaxLiveContainers` is the resource bound you tune for capacity**; the idle sweeper
  is the safety net for stale accounts, not the primary control.
- Volumes persisting across stops **and** restarts is intentional, not a leak — that is
  what makes sessions warm. They are only removed on token deletion or pool opt-out (see
  above).
- **Resetting one account's session** (e.g. after a bad CLI state): either remove the
  container and volume directly —
  ```bash
  docker rm -f naudit-session-<accountId>
  docker volume rm naudit-session-<accountId>
  ```
  and let the next review recreate them cold, or simpler — remove and re-add the
  account's token on the profile page (removing the token already triggers the same
  container+volume cleanup; re-adding it just lets the next review create a fresh one).
- **Status:** `GET /api/me/session-sandbox` (mapped only when `SessionSandbox=Docker`,
  requires sign-in) returns `{ mode, socketReachable, liveContainers }` —
  `socketReachable` is the sweeper's last ping result and `liveContainers` is the total
  count of currently running session containers across **all** accounts (not just the
  caller's own); the profile page renders it as a status line.

## Security note

Mounting `/var/run/docker.sock` into a container is **effectively root on the host**:
anything that can reach the socket can start a privileged container, mount the host
filesystem, and so on. Anyone who can reach Naudit's Docker socket owns the machine
it runs on — only enable this on a host where you already trust Naudit itself with
root-equivalent access.

Within that trust boundary, be aware that:

- the subscription token is visible in the exec's environment for the duration of a
  run (e.g. via `docker inspect`/`docker top` while it executes);
- the CLI's authenticated credentials live in the account's named volume; and
- that volume **intentionally survives** container stops and Naudit restarts (see
  Lifecycle above) — it is only removed on explicit token deletion or pool opt-out.

None of this is new risk *created* by the sandbox beyond what socket access already
implies — it is called out here so operators go in with eyes open rather than
assuming the containers add a security boundary the socket itself already gives away.

## Deployment

Mount the host's Docker socket into the Naudit container and add its group so the
non-root Naudit process can use it, then switch the mode on:

```yaml
services:
  naudit:
    image: ghcr.io/benediktnau/naudit:latest
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    group_add:
      - "984"   # GID of the docker group on the HOST: stat -c '%g' /var/run/docker.sock
    environment:
      Naudit__Ai__SessionSandbox: "Docker"
```

Find the correct GID for `group_add` with `stat -c '%g' /var/run/docker.sock` **on the
host** (not inside the container) — it varies by distro/install and must match exactly,
or the non-root Naudit process (`$APP_UID`) cannot use the socket even though it is
mounted. If the GID is wrong or the socket isn't reachable, Naudit logs a warning and
runs every sandbox-mode session in-process instead (see Fail-open matrix) — it does not
crash or block reviews.

See [Deployment › Session sandbox (optional)](deployment.md#session-sandbox-optional)
for this same snippet in the context of the full Coolify environment template.
