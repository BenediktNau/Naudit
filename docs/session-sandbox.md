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
| `Naudit:Ai:Sandbox:RemoveTimeout` | `00:00:05` | How long a teardown (token deletion, pool opt-out, suspension) waits for the account lock before giving up. It runs on an HTTP request and must not block behind a running review; whatever it skips is cleaned up by the reconciliation pass (see Lifecycle). |
| `Naudit:Ai:Sandbox:Image` | *(empty)* | Optional image override. Empty means self-inspection: Naudit resolves its own image via `docker inspect $HOSTNAME`, so the `claude` CLI baked into the Naudit image is always used and never drifts from the running version. Deployments that override the container hostname (e.g. `--hostname`, or Compose's `hostname:`) break self-inspection, since `$HOSTNAME` then no longer matches the running container's actual name — set this key explicitly in that case. |

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
  (`sh -c '"$0" "$@" < /tmp/naudit-stdin; rc=$?; rm -f /tmp/naudit-stdin; exit $rc'`), so
  the original argv reaches `claude` unchanged with no shell-quoting risk. The wrapper
  deletes that scratch file again once the run finishes — the container outlives the
  review by days, and the file holds the (redacted) diff. It lives in `/tmp`, i.e. the
  container layer, never in the persistent session volume.
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
- **Removal on token deletion / pool opt-out / account suspension.** Deleting an
  account's stored Claude session token, turning off "share my session in the
  round-robin pool", or an admin suspending/deactivating the account (any status
  transition away from Active), removes that account's container **and** its volume.
  This is deliberate: the volume holds live CLI credentials and must not outlive the
  account's consent — or authorization — to use them. The immediate attempt is
  best-effort by design: it never blocks the underlying DB change, and it waits at
  most `RemoveTimeout` for the account lock so the HTTP request cannot hang behind a
  running review. Reactivating the account (transitioning back to Active) does not
  touch the sandbox; the next review simply starts a fresh container cold.
- **Reconciliation makes that removal durable.** Anything the immediate attempt misses —
  a Docker error, a review still holding the lock, or a Naudit crash mid-teardown —
  is caught by the sweeper: every tick it lists the existing `naudit-session-*`
  containers and removes container **and** volume for any whose account is gone, is not
  `Active`, or no longer has a stored token. So the guarantee is "immediately if
  possible, otherwise within one sweep interval (5 minutes)" rather than best-effort
  alone. Pool opt-out is not part of that rule: in `Author` mode a user legitimately
  keeps a warm session without being in the round-robin pool.
- **A running exec is never stopped out from under itself.** Both the idle sweeper and
  the LRU cap use a non-blocking lock attempt before stopping a container; if an exec
  is in flight for that account, the container is skipped that round rather than being
  killed mid-review (the LRU cap can then stay transiently over its configured limit by
  one container until the next pass).

## Fail-open matrix

A review must never fail *because of* the sandbox: every **Docker plumbing** failure
(socket, engine API, container lifecycle) falls back to the existing in-process runner.
That promise deliberately does not extend to two things which are not sandbox failures
at all — a review that exceeds its own timeout, and a `claude` process that exits
non-zero; both behave exactly as they do without the sandbox:

| Situation | Behaviour |
| --- | --- |
| Socket missing or unusable (`PingAsync` fails) | In-process fallback for every run; the sweeper re-pings every 5 minutes and switches back to sandboxed runs automatically once the socket is reachable again (self-healing, no restart needed). |
| Initial container create/start fails (Docker-plumbing error) | Immediate fallback to the in-process runner (no retry); the review proceeds. |
| Exec fails mid-session (e.g. container removed externally after a successful start) | One retry (re-`EnsureRunning`, stdin re-upload, exec again), then fallback to the in-process runner. |
| Exec exceeds the review timeout | Same semantics as the in-process runner: the container is stopped (which kills the exec) and a `TimeoutException` is thrown — this is **not** swallowed as a fallback case, it is a real timeout. |
| `claude` itself exits non-zero | Not a sandbox failure at all — it's a normal CLI error and follows the regular error path, same as an in-process run. |

Two consequences of the fallback worth knowing:

- **Worst-case wall clock is roughly twice the configured AI timeout.** The in-process
  retry deliberately gets the full timeout again rather than the remainder, so it can
  never start starved. Reviews from webhooks run on the background queue where that only
  costs time; the synchronous `POST /review` (CI) path is the one to size job timeouts
  for.
- **A container that had to fall back loses its "recently used" mark**, so the idle
  sweeper and the LRU cap can reclaim it immediately instead of it shielding itself for
  up to `IdleTimeout` while healthy accounts get evicted.

## Operations

- **`MaxLiveContainers` is the resource bound you tune for capacity**; the idle sweeper
  is the safety net for stale accounts, not the primary control.
- Volumes persisting across stops **and** restarts is intentional, not a leak — that is
  what makes sessions warm. They are only removed on token deletion, pool opt-out, or
  account suspension/deactivation (see above).
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
  requires sign-in) returns `{ mode, socketReachable }` for every signed-in user —
  `socketReachable` is the sweeper's last ping result — plus `liveContainers` **for
  admins only**, the count of currently running session containers across all accounts.
  That count is an operations number rather than self-service information, so the field
  is omitted entirely for non-admins; the profile page renders whatever it gets as a
  status line.

## Security note

Mounting `/var/run/docker.sock` into a container is **effectively root on the host**:
anything that can reach the socket can start a privileged container, mount the host
filesystem, and so on. Anyone who can reach Naudit's Docker socket owns the machine
it runs on — only enable this on a host where you already trust Naudit itself with
root-equivalent access. ([The Docker socket](docker-socket.md) covers this boundary,
who else uses the socket, and the bare-metal setup.)

That is the outer boundary. Inside it, the sandbox does change the shape of the risk
compared to in-process runs, and those changes are real rather than implied by socket
access alone:

- **Credentials gain a resting place.** In-process runs authenticate into a throwaway
  `CLAUDE_CONFIG_DIR` that dies with the process. Here the CLI's authenticated state
  lives in the account's named volume and **intentionally survives** container stops and
  Naudit restarts — that persistence *is* the feature. It is removed on token deletion,
  pool opt-out, or suspension/deactivation, immediately where possible and otherwise by
  the reconciliation pass (see Lifecycle) — but a volume that Docker refuses to delete
  and that reconciliation never sees again (e.g. Naudit is switched back to
  `SessionSandbox=None` with containers still around) stays on the host until someone
  removes it by hand.
- **The token becomes inspectable from a second angle.** It is passed in the exec's
  environment and is therefore visible via `docker inspect`/`docker top` for the
  duration of a run, in addition to the process environment it always had.
- **What the sandbox does not do is add a boundary.** Per-account containers separate
  accounts from *each other*, which in-process runs did not; they do not isolate
  anything from an operator or attacker who can reach the Docker socket.

The honest summary: the containers buy inter-account isolation and warm sessions, and
they cost you persistent credential storage on the host. Enable it where you already
trust Naudit with root-equivalent access, and treat the session volumes as secret
material when backing up or decommissioning a host.

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
