# Chart changelog

Versions of the **chart** (`Chart.yaml: version`), not of Naudit itself — the
app version is whatever `image.tag` points at.

## 0.2.0

- **Changed default, act before upgrading:** `image.pullPolicy` is now empty by
  default and derived from the tag — `Always` for the mutable `latest`,
  `IfNotPresent` for a pinned tag. Installs that ran on `latest` previously kept
  whatever image the node had cached; they will now pull on every rollout, which
  is the point (a `rollout restart` could otherwise start a different version
  than the one you thought was running). Set `image.pullPolicy` explicitly to
  keep the old behaviour — an explicit value always wins.
- **Added:** `dind.*` — an optional Docker-in-Docker sidecar that supplies the
  engine for dynamic testing (DAST). Off by default; with `dind.enabled=false`
  the chart renders exactly as in 0.1.0. Requires `privileged: true`, so read
  the security note in `README.md` first.
- **Added:** the hardening pointer for `Naudit__ForwardedHeaders__KnownNetworks__0`
  in the `extraEnv` example — without it Naudit trusts `X-Forwarded-*` from any
  source.

## 0.1.0

Initial chart: single replica, `Recreate`, SQLite on a PVC by default,
bootstrap-only env (everything else is configured on the Settings page).
