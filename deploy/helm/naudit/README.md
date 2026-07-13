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

## Upgrade / values

```bash
helm upgrade naudit deploy/helm/naudit -n naudit --reuse-values --set image.tag=v0.1.13
```

All knobs (persistence size/class, resources, extraEnv overrides, TLS,
nodeSelector/tolerations/affinity) are documented inline in
[`values.yaml`](values.yaml). `replicas` is deliberately fixed at 1 — SQLite is
single-writer, the review queue is in-memory, and the Settings restart loop is
per-process.
