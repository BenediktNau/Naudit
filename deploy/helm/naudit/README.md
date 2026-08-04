# Naudit Helm chart

Same setup as the plain manifests in [`deploy/k8s/`](../../k8s/README.md)
(1 replica / `Recreate`, SQLite on a PVC by default, bootstrap-only env —
everything else is configured on the Settings page after first start), packaged
as a chart. See [docs/deployment.md](../../../docs/deployment.md) for the
container facts and [docs/configuration.md](../../../docs/configuration.md) for
the `Naudit:*` keys.

## Install

```bash
# Pull secret — the ghcr package is private by default (PAT with read:packages);
# skip if you made the package public.
kubectl create namespace naudit
kubectl -n naudit create secret docker-registry ghcr-pull \
  --docker-server=ghcr.io --docker-username=<github-user> --docker-password=<pat>

helm install naudit deploy/helm/naudit \
  --namespace naudit \
  --set ingress.host=naudit.example.internal \
  --set ingress.className=nginx \
  --set image.tag=v0.1.12 \
  --set imagePullSecrets[0].name=ghcr-pull \
  --set admin.initialPassword='<choose-a-strong-password>'
```

⚠️ `--set admin.initialPassword` is fine for a throwaway install and nothing
else: the value lands in your shell history, in the process list while `helm`
runs, and in the release's stored values — `helm get values naudit` prints it
back in clear text. Use the pre-created secret below for anything you keep.

The chart then renders the bootstrap secret itself. For production prefer a
pre-created secret so the password never enters the Helm release history:

```bash
kubectl -n naudit create secret generic naudit-bootstrap \
  --from-literal=admin-initial-password='<secret>'
helm install naudit deploy/helm/naudit -n naudit \
  --set bootstrap.existingSecret=naudit-bootstrap ...
```

## Postgres instead of SQLite

```bash
kubectl -n naudit create secret generic naudit-bootstrap \
  --from-literal=admin-initial-password='<secret>' \
  --from-literal=db-connection-string='Host=<db-host>;Port=5432;Database=naudit;Username=naudit;Password=<secret>'
helm install naudit deploy/helm/naudit -n naudit \
  --set db.provider=Postgres \
  --set bootstrap.existingSecret=naudit-bootstrap ...
```

No PVC is created in this mode.

## Keeping environment values out of this repo

This repo may be mirrored to a public origin — so the chart here stays generic
(placeholders only) and your real environment values (internal hostnames, image
tag, resources) live in a **separate deploy repo** that exists only on your
internal GitLab and is never mirrored. `.gitignore` blocks `values-*.yaml`
under this directory as a safety net against committing them here by accident.

Suggested layout of the internal deploy repo:

```
naudit-deploy/                # internal GitLab only, never mirrored
├── values-prod.yaml          # host, image tag, resources — NO secrets
└── .gitlab-ci.yml            # deploy job (below)
```

Secrets never belong in either repo: use `bootstrap.existingSecret` (pre-created
`kubectl` secret) — or SOPS/SealedSecrets in the deploy repo if you want them
GitOps-managed.

Deploy job (runner needs cluster access, e.g. via the GitLab agent for
Kubernetes; `NAUDIT_REF` pins the chart version = a naudit tag):

```yaml
deploy:
  image:
    name: alpine/helm:3.16.4
    entrypoint: [""]
  variables:
    NAUDIT_REF: v0.1.12
  script:
    - apk add --no-cache git
    - git clone --depth 1 --branch "$NAUDIT_REF" https://gitlab.example.internal/<group>/naudit.git /tmp/naudit
    - helm upgrade --install naudit /tmp/naudit/deploy/helm/naudit
        --namespace naudit --create-namespace
        -f values-prod.yaml
  rules:
    - if: $CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH
  environment: production
```

The same split works with ArgoCD/Flux instead of CI: point the Application at
the deploy repo for values and at the (mirrored) naudit repo for the chart
(ArgoCD multi-source), or wrap naudit as a dependency in a small umbrella chart
inside the deploy repo.

## Dynamic testing (DAST) — the `dind` sidecar

DAST builds and runs the PR's own container, so it needs a Docker engine.
On a single host that is the mounted `/var/run/docker.sock`; in Kubernetes
there is no such socket to mount (containerd since 1.24), and Naudit's engine
client speaks Unix sockets only — `DOCKER_HOST=tcp://…` is not an option.
The chart therefore runs a Docker-in-Docker sidecar and shares its socket with
the app container through an `emptyDir`:

```bash
helm upgrade naudit deploy/helm/naudit -n naudit --reuse-values \
  --set dind.enabled=true \
  --set dind.storage.persistent=true   # keeps the probe image across restarts
```

This wires the plumbing only — it sets `Naudit__Review__Dast__DockerSocketPath`
(and the session-sandbox twin, which would otherwise win over it). The feature
itself is DB-managed: switch it on under Settings → Review rules together with
the project allowlist, then restart from the same page.

Three things worth knowing before you flip it:

- **`privileged: true` is unavoidable** for a real daemon (cgroup mounts,
  overlayfs, iptables) and is effectively root on the node. The namespace must
  not enforce a `baseline`/`restricted` Pod Security Standard. The Naudit
  container itself stays non-root with `cap-drop: ALL`.
- **`/var/lib/docker` must be a volume** (the chart mounts one). On the
  containerd overlay rootfs, `overlay2` cannot operate and dockerd falls back
  to `vfs` — slow and enormous.
- **The sidecar pulls images from the pod network**: the probe image
  (`mcr.microsoft.com/playwright/mcp`, ~2 GB) and whatever base images the PR's
  Dockerfile references. Verify that egress exists before enabling — a blocked
  registry surfaces only as "no dynamic findings" (DAST fails open).

Verify after the rollout:

```bash
kubectl -n naudit exec deploy/naudit -c naudit -- ls -l /var/run/naudit-docker/docker.sock
# expected: srw-rw---- 1 root 1654 … — group must be the app UID, else --group missed
kubectl -n naudit exec deploy/naudit -c dind -- docker pull mcr.microsoft.com/playwright/mcp:latest
```

## Upgrade / values

```bash
helm upgrade naudit deploy/helm/naudit -n naudit --reuse-values --set image.tag=v0.1.13
```

All knobs (persistence size/class, resources, extraEnv overrides, TLS,
nodeSelector/tolerations/affinity) are documented inline in
[`values.yaml`](values.yaml). `replicas` is deliberately fixed at 1 — SQLite is
single-writer, the review queue is in-memory, and the Settings restart loop is
per-process.
