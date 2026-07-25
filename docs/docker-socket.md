# The Docker socket (`/var/run/docker.sock`)

Two optional Naudit features drive the host's Docker engine: the
[session sandbox](session-sandbox.md) (released) and [DAST](dast.md) (app-runner shipped; probing analyzer in development).
Both talk to the engine through the same seam — this page explains what that socket
access means, what it is used for, and how to set it up. It is the shared reference for
both features.

## What it is

`/var/run/docker.sock` is the **Unix socket of the Docker Engine API** on the host.
Anything that can reach it can drive the engine: start and stop containers, build
images, create volumes and networks — everything the `docker` CLI does, because the CLI
itself is just a client of this socket.

A process with socket access does not start containers *inside* itself; it starts
**sibling containers** directly on the host, next to its own. No Docker-in-Docker
involved.

## The honest security picture

> **Socket access is effectively root on the host.**

Whoever reaches the socket can start a privileged container and mount the host
filesystem through it — at that point the machine is theirs. There is no configuration
that grants "a little bit" of socket access: giving Naudit the socket means trusting
Naudit (and anyone who compromises Naudit) with root-equivalent access to that host.

Rule of thumb: only do this on a host where you would already trust Naudit with root —
for example a dedicated VM for the review bot, not a shared production host with other
critical workloads.

## What Naudit uses it for

| Feature | Switch | What gets started |
| --- | --- | --- |
| [Session sandbox](session-sandbox.md) | `Naudit:Ai:SessionSandbox=Docker` | Long-lived per-account containers for Claude subscription sessions (your own, trusted workloads) |
| [DAST](dast.md) (app-runner shipped; probing analyzer in development) | `Naudit:Review:Dast:Enabled=true` | Short-lived per-review containers that build and run the **PR's own app** — foreign code, with its own containment story |

Both switches are independent and off by default — different risk profiles, separately
enabled. Everything is **fail-open** on Docker plumbing: no socket (or any engine error)
means sandbox sessions run in-process and DAST yields no findings; a review never fails
because of the Docker substrate.

## Setup

Naudit's Docker client speaks Unix sockets only — **Linux hosts only**; the `win-x64`
release binaries cannot use these features.

### Naudit runs as a container (Coolify, Compose)

The socket must be mounted in, and because Naudit runs as a non-root user, the process
needs the **GID of the host's `docker` group**:

```bash
# On the HOST (not inside a container) — the GID varies by distro:
stat -c '%g' /var/run/docker.sock
```

```yaml
services:
  naudit:
    image: ghcr.io/benediktnau/naudit:latest
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    group_add:
      - "984"   # ← the GID from the stat command above
```

In Coolify the reliable route is a **Docker Compose resource**, because `group_add` is
declarable there. Image/Dockerfile resources can add the socket as a volume mount under
*Storages*, but expose no `group_add` field — switch to Compose in that case. A wrong
GID is not fatal: the socket is mounted but unusable, Naudit logs a warning and runs
fail-open (see above).

### Naudit runs as a bare process (release binaries, `dotnet run`)

No mount, no `group_add` — the process opens the host socket directly. Requirements:

- a running Docker engine on the same host;
- the Naudit user in the `docker` group (which is itself root-equivalent — the trust
  decision is identical, only the mounting ceremony disappears);
- for the **session sandbox**: `Naudit:Ai:Sandbox:Image` must be set explicitly
  (e.g. `ghcr.io/benediktnau/naudit:latest`). The default resolves Naudit's own image
  by self-inspection, which only works when Naudit itself is a container.

DAST is designed to behave identically in both deployment forms: all interaction with
its review network runs through the socket (`docker exec`), never over published ports.

## See also

- [Deployment](deployment.md) — the full Coolify environment template.
- [Session sandbox](session-sandbox.md) — lifecycle, fail-open matrix, and what
  per-account containers change about credential storage.
- [DAST](dast.md) — status and the activation guide.
