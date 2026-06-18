# Docker-Image + Release-Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Naudit containerisieren und bei jedem Merge auf `main` automatisch ein SemVer-Release + ein Docker-Image in `ghcr.io` erzeugen; zusätzlich ein PR-CI-Gate (build + test).

**Architecture:** Reine Infrastruktur, kein App-Code. Ein multi-stage `Dockerfile` (SDK baut → ASP.NET-Runtime läuft, non-root) publisht das Web-Projekt. Zwei GitHub-Actions-Workflows: `ci.yml` (PR-Gate: build+test) und `release.yml` (push-auf-main: test-Gate → Versionsbestimmung → docker build/push → git-tag + Release). Die Versionslogik wird in ein kleines, lokal testbares Shell-Skript `.github/scripts/next-version.sh` ausgelagert (gegenüber dem Spec eine bewusste Verfeinerung: macht die Patch-Bump-Logik unit-testbar statt nur als Inline-YAML-Shell zu existieren). Coolify deployt selbst — kein Deploy-Step in der CI.

**Tech Stack:** .NET 10 (`mcr.microsoft.com/dotnet/{sdk,aspnet}:10.0`), Docker (multi-stage), GitHub Actions, GitHub Container Registry (`ghcr.io`), Bash.

---

## File Structure

| Datei | Verantwortung | Status |
|---|---|---|
| `Dockerfile` (Repo-Root) | Multi-stage Build → schlankes, non-root Runtime-Image | Create |
| `.dockerignore` (Repo-Root) | Build-Context klein/deterministisch halten | Create |
| `.github/scripts/next-version.sh` | Nächste `vX.Y.Z` aus letztem Tag (Patch+1, Seed `v0.1.0`) | Create |
| `.github/workflows/ci.yml` | PR-Gate: `dotnet build` + `dotnet test` | Create |
| `.github/workflows/release.yml` | Merge-auf-main: test-Gate → Version → image build/push → tag + Release | Create |
| `README.md` | Abschnitt „Deployment / Container" (Image-Pfad, Tags, Versionierung) | Modify |
| `CLAUDE.md` | Kurzhinweis auf Pipeline + Image unter „Architecture" | Modify |

**Image-Koordinaten (fix, überall identisch verwenden):** `ghcr.io/benediktnau/naudit`
(GitHub-Owner ist `BenediktNau`; ghcr verlangt **lowercase** → `benediktnau`).

**Verifikations-Tooling lokal verfügbar:** `docker` 29.x, `dotnet` 10.0.x, `python3`. `actionlint`/`yamllint` sind **nicht** installiert — Workflow-YAML wird lokal nur grob (PyYAML-Parse, best effort) geprüft; die echte Validierung ist der erste PR-Run (`ci.yml`) bzw. der erste `release.yml`-Lauf (Merge auf `main`).

---

## Task 1: Dockerfile + .dockerignore

**Files:**
- Create: `Dockerfile`
- Create: `.dockerignore`

Hintergrund: Das Web-Projekt (`src/Naudit.Web/Naudit.Web.csproj`) referenziert `Naudit.Infrastructure` und `Naudit.Core`. `dotnet publish` auf das Web-Csproj zieht beide referenzierten Projekte automatisch mit. Die App startet ohne Secrets sauber (IChatClient/IGitPlatform sind lazy), `GET /health` liefert den Text `healthy`. Container-Port ist 8080 (ASP.NET-Default im Container seit .NET 8).

- [ ] **Step 1: `.dockerignore` schreiben**

Create `.dockerignore`:

```gitignore
# Build-Artefakte und IDE-/VCS-Kram aus dem Build-Context fernhalten.
**/bin/
**/obj/
.git/
.github/
docs/
tests/
**/*.user
ngrok-*.tgz
.gitignore
LICENSE
README.md
```

- [ ] **Step 2: `Dockerfile` schreiben**

Create `Dockerfile`:

```dockerfile
# syntax=docker/dockerfile:1

# --- Build-Stage: SDK kompiliert und published das Web-Projekt ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Zuerst nur die Projektdateien kopieren und restoren -> stabiler Layer-Cache,
# solange sich die Abhaengigkeiten nicht aendern.
COPY src/Naudit.Core/Naudit.Core.csproj src/Naudit.Core/
COPY src/Naudit.Infrastructure/Naudit.Infrastructure.csproj src/Naudit.Infrastructure/
COPY src/Naudit.Web/Naudit.Web.csproj src/Naudit.Web/
RUN dotnet restore src/Naudit.Web/Naudit.Web.csproj

# Restlichen Quellcode kopieren und Release publishen (zieht Infrastructure+Core mit).
COPY src/ src/
RUN dotnet publish src/Naudit.Web/Naudit.Web.csproj -c Release -o /app/publish --no-restore

# --- Runtime-Stage: schlankes ASP.NET-Image, non-root ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Vom Base-Image bereitgestellter non-root-User.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Naudit.Web.dll"]
```

- [ ] **Step 3: Image bauen (muss durchlaufen)**

Run: `docker build -t naudit:dev .`
Expected: Build endet mit `naming to docker.io/library/naudit:dev` bzw. `writing image ... done` — kein Fehler. (Erster Lauf zieht die Base-Images, das ist normal.)

- [ ] **Step 4: Container starten und Health prüfen**

Run:
```bash
docker run --rm -d -p 18080:8080 --name naudit-smoke naudit:dev
sleep 3
curl -fsS http://localhost:18080/health; echo
docker rm -f naudit-smoke
```
Expected: `curl` gibt `healthy` aus (HTTP 200). Danach wird der Container entfernt.

- [ ] **Step 5: Non-root verifizieren (Bonus-Check)**

Run: `docker run --rm --entrypoint sh naudit:dev -c 'id -u'`
Expected: eine Zahl `!= 0` (der vom Base-Image gesetzte `$APP_UID`, typischerweise `1654`).

- [ ] **Step 6: Commit**

```bash
git add Dockerfile .dockerignore
git commit -m "feat(infra): add multi-stage Dockerfile + .dockerignore"
```

---

## Task 2: Versions-Skript `next-version.sh` (lokal testbar)

**Files:**
- Create: `.github/scripts/next-version.sh`

Hintergrund: Die Logik „letzter `vX.Y.Z`-Tag, Patch +1, sonst Seed `v0.1.0`" wird als eigenes Skript isoliert, damit sie ohne GitHub-Runner getestet werden kann. Annahme (POC): vorhandene Tags sind wohlgeformt `vX.Y.Z`. Das Skript gibt **nur** die Version auf stdout aus.

- [ ] **Step 1: Test (manuell) zuerst ausführen — Skript existiert noch nicht → muss fehlschlagen**

Run:
```bash
bash .github/scripts/next-version.sh
```
Expected: FAIL — `bash: .github/scripts/next-version.sh: No such file or directory`.

- [ ] **Step 2: Skript schreiben**

Create `.github/scripts/next-version.sh`:

```bash
#!/usr/bin/env bash
# Bestimmt die naechste Release-Version:
#   - letzter vX.Y.Z-Tag mit Patch+1
#   - oder v0.1.0, wenn noch kein passender Tag existiert
# Gibt ausschliesslich "vX.Y.Z" auf stdout aus. Annahme: Tags sind wohlgeformt vX.Y.Z.
set -euo pipefail

last="$(git describe --tags --abbrev=0 --match 'v*' 2>/dev/null || true)"

if [ -z "$last" ]; then
  echo "v0.1.0"
  exit 0
fi

version="${last#v}"            # vX.Y.Z -> X.Y.Z
IFS='.' read -r major minor patch <<< "$version"
echo "v${major}.${minor}.$((patch + 1))"
```

- [ ] **Step 3: Test — kein Tag ⇒ `v0.1.0`**

Run:
```bash
SCRIPT="$(pwd)/.github/scripts/next-version.sh"
tmp="$(mktemp -d)"; ( cd "$tmp" && git init -q && git commit --allow-empty -qm init && bash "$SCRIPT" ); rm -rf "$tmp"
```
Expected: `v0.1.0`

- [ ] **Step 4: Test — vorhandener Tag `v1.2.3` ⇒ `v1.2.4`**

Run:
```bash
SCRIPT="$(pwd)/.github/scripts/next-version.sh"
tmp="$(mktemp -d)"; ( cd "$tmp" && git init -q && git commit --allow-empty -qm init && git tag v1.2.3 && bash "$SCRIPT" ); rm -rf "$tmp"
```
Expected: `v1.2.4`

- [ ] **Step 5: Test — höchster Tag gewinnt (`v0.1.0` und `v0.2.5` ⇒ `v0.2.6`)**

Run:
```bash
SCRIPT="$(pwd)/.github/scripts/next-version.sh"
tmp="$(mktemp -d)"; ( cd "$tmp" && git init -q \
  && git commit --allow-empty -qm a && git tag v0.1.0 \
  && git commit --allow-empty -qm b && git tag v0.2.5 \
  && bash "$SCRIPT" ); rm -rf "$tmp"
```
Expected: `v0.2.6`
(Hinweis: `git describe --tags --abbrev=0` liefert den jüngsten *erreichbaren* Tag in der Commit-Historie; bei linearer History ist das der zuletzt gesetzte.)

- [ ] **Step 6: Commit**

```bash
git add .github/scripts/next-version.sh
git commit -m "feat(ci): add next-version.sh (patch-bump SemVer, seed v0.1.0)"
```

---

## Task 3: PR-CI-Workflow `ci.yml`

**Files:**
- Create: `.github/workflows/ci.yml`

Hintergrund: Gate für PRs gegen `main` — `main` soll nicht rot werden. Nur build + test, kein Docker/Release.

- [ ] **Step 1: Workflow schreiben**

Create `.github/workflows/ci.yml`:

```yaml
name: CI

on:
  pull_request:
    branches: [main]

jobs:
  build-test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Restore
        run: dotnet restore Naudit.slnx

      - name: Build
        run: dotnet build Naudit.slnx -c Release --no-restore

      - name: Test
        run: dotnet test Naudit.slnx -c Release --no-build
```

- [ ] **Step 2: YAML lokal grob validieren (best effort)**

Run:
```bash
python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml')); print('ci.yml: YAML ok')" 2>&1 || echo "PyYAML fehlt — Validierung erfolgt beim ersten PR-Run"
```
Expected: `ci.yml: YAML ok` (oder der Fallback-Hinweis, falls PyYAML fehlt — das ist akzeptabel, die echte Prüfung ist der PR-Run dieses Features).

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add PR build+test gate (ci.yml)"
```

---

## Task 4: Release-Workflow `release.yml`

**Files:**
- Create: `.github/workflows/release.yml`

Hintergrund: Bei push auf `main` (PR-Merge): Tests als Gate, dann Version via `next-version.sh`, dann Image bauen+pushen nach `ghcr.io/benediktnau/naudit`, dann git-Tag + GitHub-Release. Auth über das eingebaute `GITHUB_TOKEN`. `workflow_dispatch` erlaubt manuelles Auslösen — das ist **kein** Trockenlauf, sondern ein echter Release (Image-Push + Tag + Release).

- [ ] **Step 1: Workflow schreiben**

Create `.github/workflows/release.yml`:

```yaml
name: Release

on:
  push:
    branches: [main]
  workflow_dispatch:

permissions:
  contents: write   # git-Tag pushen + GitHub-Release anlegen
  packages: write   # Image nach ghcr.io pushen

jobs:
  release:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0   # alle Tags fuer next-version.sh / git describe

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      # --- Gate: Tests muessen gruen sein, sonst kein Release/Image ---
      - name: Restore
        run: dotnet restore Naudit.slnx

      - name: Build
        run: dotnet build Naudit.slnx -c Release --no-restore

      - name: Test
        run: dotnet test Naudit.slnx -c Release --no-build

      # --- Naechste Version bestimmen ---
      - name: Determine version
        id: version
        run: |
          version="$(bash .github/scripts/next-version.sh)"
          echo "version=$version" >> "$GITHUB_OUTPUT"
          echo "short_sha=$(git rev-parse --short HEAD)" >> "$GITHUB_OUTPUT"
          echo "Next version: $version"

      # --- Image bauen und nach ghcr pushen ---
      - uses: docker/setup-buildx-action@v3

      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - uses: docker/build-push-action@v6
        with:
          context: .
          file: ./Dockerfile
          push: true
          tags: |
            ghcr.io/benediktnau/naudit:${{ steps.version.outputs.version }}
            ghcr.io/benediktnau/naudit:latest
            ghcr.io/benediktnau/naudit:sha-${{ steps.version.outputs.short_sha }}

      # --- Git-Tag + GitHub-Release (nur nach erfolgreichem Push) ---
      - name: Tag and release
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          git tag "${{ steps.version.outputs.version }}"
          git push origin "${{ steps.version.outputs.version }}"
          gh release create "${{ steps.version.outputs.version }}" --generate-notes
```

- [ ] **Step 2: YAML lokal grob validieren (best effort)**

Run:
```bash
python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/release.yml')); print('release.yml: YAML ok')" 2>&1 || echo "PyYAML fehlt — Validierung erfolgt beim ersten release.yml-Lauf"
```
Expected: `release.yml: YAML ok` (oder der Fallback-Hinweis).

- [ ] **Step 3: Image-Pfad-Konsistenz prüfen (lowercase, 3 Tags)**

Run: `grep -n 'ghcr.io/benediktnau/naudit' .github/workflows/release.yml`
Expected: genau 3 Treffer (`:${{ ...version }}`, `:latest`, `:sha-${{ ...short_sha }}`), alle lowercase.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "ci: add release pipeline (test-gate, ghcr image, tag+release)"
```

---

## Task 5: Dokumentation (README + CLAUDE.md)

**Files:**
- Modify: `README.md`
- Modify: `CLAUDE.md`

Hintergrund: Repo-Konvention — jedes Feature dokumentiert sich (vorherige Features ergänzten CLAUDE.md/docs). Kein App-Code, nur Doku.

- [ ] **Step 1: README-Abschnitt „Deployment / Container" ergänzen**

Füge in `README.md` einen Abschnitt ein (am Ende, vor evtl. vorhandenem Lizenz-/Schluss-Block — sonst ans Dateiende):

```markdown
## Deployment / Container

Naudit wird als Container ausgeliefert. Bei jedem Merge auf `main` baut die
GitHub-Actions-Pipeline (`.github/workflows/release.yml`) — **nur wenn die Tests grün sind** —
ein Image und published es in die GitHub Container Registry:

```
ghcr.io/benediktnau/naudit:vX.Y.Z   # die Release-Version
ghcr.io/benediktnau/naudit:latest   # immer der letzte main-Stand
ghcr.io/benediktnau/naudit:sha-XXXX # exakter Commit (Rueckverfolgbarkeit)
```

**Versionierung:** Auto-increment SemVer-Patch aus dem letzten `vX.Y.Z`-Tag
(Seed `v0.1.0`). Major/Minor bumpt man manuell durch ein eigenes Tag, z. B.
`git tag v0.2.0 && git push origin v0.2.0`; der Auto-Bump zählt darauf weiter.

**Lokal bauen/starten:**

```bash
docker build -t naudit:dev .
docker run --rm -p 8080:8080 naudit:dev
curl http://localhost:8080/health   # -> healthy
```

Das Deployment selbst übernimmt Coolify (zieht `:latest` bzw. die dort
eingestellte Tag-Policy). Die CI deployt nicht aktiv.

**PR-Gate:** `.github/workflows/ci.yml` baut und testet jeden PR gegen `main`.
```

- [ ] **Step 2: CLAUDE.md-Hinweis ergänzen**

Füge in `CLAUDE.md` unter „## Architecture" (nach der Beschreibung von `Naudit.Web`) einen kurzen Absatz ein:

```markdown
### CI/CD & Container

`Dockerfile` (Repo-Root, multi-stage: SDK baut → ASP.NET-Runtime, non-root, Port 8080)
containerisiert das Web-Projekt. Zwei GitHub-Actions-Workflows: `ci.yml` (PR-Gate: build+test)
und `release.yml` (push auf `main`: test-Gate → `.github/scripts/next-version.sh` bestimmt die
nächste SemVer-Patch-Version → Image-Build/Push nach `ghcr.io/benediktnau/naudit`
(`vX.Y.Z`/`latest`/`sha-…`) → git-Tag + GitHub-Release). Deployment macht Coolify selbst.
Kein App-Code betroffen.
```

- [ ] **Step 3: Geänderte Doku sichten**

Run: `git diff --stat README.md CLAUDE.md`
Expected: beide Dateien als geändert gelistet, keine anderen Dateien.

- [ ] **Step 4: Commit**

```bash
git add README.md CLAUDE.md
git commit -m "docs: document container build + release/CI pipeline"
```

---

## Verifikation nach allen Tasks (post-merge, manuell)

Diese Schritte laufen erst **auf GitHub** und gehören nicht in die TDD-Schleife — sie sind die
echte End-to-End-Bestätigung (vgl. Spec „Tests / Verifikation"):

1. **PR öffnen** (Feature-Branch → `main`): `ci.yml` muss grün durchlaufen (build + test).
2. **Hinweis `workflow_dispatch`:** Der Workflow ist auch manuell über die Actions-UI
   auslösbar. Das ist **kein** Trockenlauf — ein Dispatch pusht ein Image und legt Tag +
   Release real an (keine `if`-Guards). Daher nicht vor dem gewünschten ersten Release auf
   `main` dispatchen; das erste Release wird `v0.1.0`.
3. **Nach Merge auf `main`:** `release.yml` läuft automatisch; danach prüfen:
   - Package `naudit` erscheint unter `ghcr.io/benediktnau/naudit` mit Tags `v0.1.0`, `latest`, `sha-…`.
   - Git-Tag `v0.1.0` und ein GitHub-Release mit Auto-Notes existieren.
4. **Coolify** kann anschließend `ghcr.io/benediktnau/naudit:latest` ziehen (Coolify-seitig konfiguriert).

---

## Bewusste Grenzen / Non-Goals (aus dem Spec)

- Kein aktives Coolify-Deploy aus der CI; kein Coolify-Secret in GitHub.
- Patch-only Auto-Bump; major/minor per manuellem Tag.
- Kein Multi-Arch-Build (nur `linux/amd64`), kein Image-Signing/SBOM.
- Keine Test-Matrix (ein SDK, ein OS).
- Keine Änderung am App-Code der drei .NET-Projekte.

## Verweise

- Spec: `docs/superpowers/specs/2026-06-18-docker-release-pipeline-design.md`
- Repo-Architektur & Befehle: `CLAUDE.md`
