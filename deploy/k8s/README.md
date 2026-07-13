# Kubernetes deployment

Plain manifests (kustomize-applyable) for running Naudit in a Kubernetes cluster.
For the general container facts (image, port 8080, `/health`, env template) see
[docs/deployment.md](../../docs/deployment.md); for the meaning of each `Naudit:*`
key see [docs/configuration.md](../../docs/configuration.md).

> Prefer Helm (or deploy via ArgoCD/Flux)? The same setup is available as a chart
> in [`deploy/helm/naudit`](../helm/naudit) — one `helm install` instead of the
> steps below.

Design choices baked into these manifests:

- **1 replica, `Recreate` strategy** — SQLite is single-writer, the review queue is
  in-memory, and the Settings restart loop is per-process. Do not scale up.
- **SQLite on a PVC at `/data`** (default). With Postgres the PVC goes away — see below.
- **Bootstrap-only env** — DB + seed admin. Everything else (platform, tokens, AI
  provider, webhook secret) is configured on the Settings page after first start and
  lives in the database.

## 1. Adjust the placeholders

- `ingress.yaml`: set `host` (and `ingressClassName`, TLS block) to your environment.
- `deployment.yaml`: pin `image:` to a released version (e.g. `:v0.1.12`) instead of `:latest`.

## 2. Create namespace + secrets (not committed, by design)

```bash
kubectl apply -f deploy/k8s/namespace.yaml

# Pull secret — the ghcr package is private by default. Use a GitHub PAT with
# read:packages, or make the package public and delete the imagePullSecrets
# block from deployment.yaml instead.
kubectl -n naudit create secret docker-registry ghcr-pull \
  --docker-server=ghcr.io \
  --docker-username=<github-user> \
  --docker-password=<PAT-with-read:packages>

# Bootstrap secret — initial password for the seeded admin account
# (only used on first start against an empty DB; change it after login).
kubectl -n naudit create secret generic naudit-bootstrap \
  --from-literal=admin-initial-password='<choose-a-strong-password>'
```

## 3. Apply

```bash
kubectl apply -k deploy/k8s/
kubectl -n naudit get pods -w          # wait for 1/1 Running
kubectl -n naudit logs deploy/naudit   # first start runs the DB migrations
```

## 4. First start

1. Open `https://<host>` and sign in as `admin` with the initial password.
2. Configure platform (GitLab/GitHub), tokens, webhook secret, and AI provider on the
   **Settings** page, then use its **Restart** button — the host rebuilds in-process,
   the pod does not restart.
3. Point the platform webhook at `https://<host>/webhook/gitlab` (or `/webhook/github`)
   — see [docs/platform-setup.md](../../docs/platform-setup.md). The GitLab/GitHub
   instance must be able to reach the ingress host.

## Postgres instead of SQLite

In `deployment.yaml`, swap the bootstrap env for the commented Postgres block
(`Naudit__Db__Provider=Postgres` + connection string from the secret), remove the
`data` volume/volumeMount, and drop `pvc.yaml` from `kustomization.yaml`:

```bash
kubectl -n naudit create secret generic naudit-bootstrap \
  --from-literal=admin-initial-password='<secret>' \
  --from-literal=db-connection-string='Host=<db-host>;Port=5432;Database=naudit;Username=naudit;Password=<secret>'
```

## Updating

Bump the image tag in `deployment.yaml` and `kubectl apply -k deploy/k8s/`
(or `kubectl -n naudit rollout restart deploy/naudit` when tracking `:latest`).
`Recreate` means a few seconds of downtime per update. Settings, accounts, review
history, and the session signing keys all live in the database, so they survive
pod restarts and updates.
