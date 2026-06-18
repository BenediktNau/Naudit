# Design: Docker-Image + Release-Pipeline (GitHub Actions → ghcr.io)

*2026-06-18 · Projekt: Naudit*

## Ziel

Naudit containerisieren und bei jedem **Merge auf `main`** automatisch ausliefern: Tests laufen,
und **bei grün** entstehen (a) ein neues **SemVer-Release** (Patch-Bump) und (b) ein **Docker-Image**
in der **GitHub Container Registry** (`ghcr.io`). Das eigentliche Deployment macht **Coolify selbst**
(zieht das Image / Coolify-Auto-Deploy) — die CI deployt nicht aktiv.

Zusätzlich ein **PR-CI-Workflow** (build + test bei jedem PR gegen `main`), damit `main` gar nicht
erst rot wird.

## Entscheidungen

- **Trigger Release:** `on: push: branches: [main]` — greift bei PR-Merges (und direkten Pushes).
- **Registry:** `ghcr.io/benediktnau/naudit`. Auth in Actions über das eingebaute `GITHUB_TOKEN`
  (kein zusätzliches Secret). Coolify zieht mit einem eigenen Read-Token (Coolify-seitig konfiguriert).
- **Versionierung:** Auto-increment **SemVer Patch** aus dem letzten `vX.Y.Z`-Tag; existiert kein Tag,
  Seed **`v0.1.0`**. **Major/Minor** bumpt man bei Bedarf manuell durch ein eigenes Tag
  (`git tag vX.Y.0 && git push --tags`); der Auto-Bump zählt darauf weiter.
- **Image-Tags:** `vX.Y.Z` (die Release-Version), `latest`, `sha-<kurz>` (Rückverfolgbarkeit).
  Coolify pullt `latest` (oder die dort eingestellte Tag-Policy).
- **Tests als Gate:** Die Release-Pipeline baut/pusht das Image und legt das Release **nur** an,
  wenn `dotnet test Naudit.slnx` grün ist.
- **Kein aktiver Deploy-Step in der CI** (bewusst; Coolify-URL/-Secret bleiben auf Coolify-Seite).
- **Keine Code-Änderung an den drei .NET-Projekten** — reine Infrastruktur (Dockerfile + Workflows).

## Komponenten

### 1. `Dockerfile` (multi-stage, .NET 10)
- **build-Stage:** `mcr.microsoft.com/dotnet/sdk:10.0` — `dotnet restore Naudit.slnx`, dann
  `dotnet publish src/Naudit.Web/Naudit.Web.csproj -c Release -o /app/publish` (kein `--no-restore`
  nötig; restore davor). Bewusst nur das Web-Projekt publishen (zieht Infrastructure/Core mit).
- **runtime-Stage:** `mcr.microsoft.com/dotnet/aspnet:10.0` — Inhalt aus `/app/publish`,
  **non-root** User (`USER $APP_UID`, vom Base-Image bereitgestellt), `EXPOSE 8080`
  (ASP.NET-Default-Port im Container seit .NET 8), `ENTRYPOINT ["dotnet", "Naudit.Web.dll"]`.
- Liegt im Repo-Root (`/Dockerfile`), Build-Context = Repo-Root.

### 2. `.dockerignore` (Repo-Root)
- Schließt `bin/`, `obj/`, `.git/`, `docs/`, `tests/`, `**/*.user`, `ngrok-*.tgz` und vorhandene
  Build-Artefakte aus — hält den Build-Context klein und deterministisch.

### 3. `.github/workflows/release.yml`
- **Trigger:** `push` auf `main`; zusätzlich `workflow_dispatch` (manueller Trockenlauf).
- **Permissions:** `contents: write` (Tag + Release), `packages: write` (ghcr-Push).
- **Ein Job `release` (Ubuntu, fail-fast, sequenziell):**
  1. `actions/checkout` mit `fetch-depth: 0` (Tags verfügbar).
  2. `actions/setup-dotnet` (10.x) → `dotnet test Naudit.slnx -c Release`. **Gate:** rot ⇒ Abbruch.
  3. **Nächste Version bestimmen** (Shell): letzten Tag via `git describe --tags --abbrev=0 --match 'v*'`
     lesen; Patch +1; kein Tag ⇒ `v0.1.0`. Ergebnis als Step-Output (`version`, `short_sha`).
  4. `docker/login-action` → ghcr mit `${{ github.actor }}` / `${{ secrets.GITHUB_TOKEN }}`.
  5. `docker/build-push-action` (context `.`, file `Dockerfile`, `push: true`) mit Tags
     `ghcr.io/benediktnau/naudit:vX.Y.Z`, `:latest`, `:sha-<kurz>`.
  6. Git-Tag `vX.Y.Z` setzen/pushen + Release anlegen (`softprops/action-gh-release` oder
     `gh release create` mit `--generate-notes`, Tag = `vX.Y.Z`).

### 4. `.github/workflows/ci.yml` (PR-Gate)
- **Trigger:** `on: pull_request: branches: [main]`.
- **Job `build-test` (Ubuntu):** checkout → setup-dotnet 10 → `dotnet build Naudit.slnx -c Release`
  → `dotnet test Naudit.slnx -c Release`. Kein Docker/Release — nur die Korrektheits-Absicherung.

## Datenfluss

```
PR geöffnet ──▶ ci.yml: dotnet build + test            (grün = mergebar)
                     │
PR-Merge auf main ──▶ release.yml:
   1) dotnet test (Gate) ──rot──▶ Abbruch (kein Release, kein Image)
   2) Version = letzter vX.Y.Z + Patch (Seed v0.1.0)
   3) docker build + push ──▶ ghcr.io/benediktnau/naudit:{vX.Y.Z, latest, sha-…}
   4) git tag vX.Y.Z + GitHub-Release (auto-notes)
                     │
              Coolify ◀── zieht :latest und deployt selbst
```

## Tests / Verifikation
- **Dockerfile lokal:** `docker build -t naudit:dev .` baut durch; `docker run -p 8080:8080 naudit:dev`
  startet, `GET /health` → `healthy`.
- **release.yml trocken:** via `workflow_dispatch` auf einem Branch laufen lassen, bevor der erste
  echte Merge passiert (prüft Versionslogik + Push-Permissions, ohne auf einen Merge zu warten).
- **ci.yml:** wird durch den PR dieses Features selbst das erste Mal ausgeführt.

## Bewusste Grenzen / Non-Goals
- **Kein aktives Coolify-Deploy** aus der CI; kein Coolify-Secret in GitHub. (Spätere Erweiterung.)
- **Patch-only Auto-Bump** — keine Conventional-Commits-Analyse für major/minor (manuelles Tag genügt).
- **Kein Multi-Arch-Build** (nur `linux/amd64`) und **kein Image-Signing/SBOM** — POC-Scope.
- **Keine Test-Matrix** (ein .NET-SDK, ein OS).
- **Keine Änderung am App-Code** — nur Dockerfile + zwei Workflows + `.dockerignore`.

## Verweise
- Repo-Architektur: `CLAUDE.md`
- Vault-Board-Item: „Docker + Deploy in coolify dadurch einfacher in gitlab auch integrierbar"
- Vorheriger Spec: `docs/superpowers/specs/2026-06-18-ci-review-endpoint-design.md`
