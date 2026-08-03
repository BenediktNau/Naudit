# syntax=docker/dockerfile:1

# --- Build-Stage: SDK kompiliert und published das Web-Projekt ---
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
WORKDIR /src

# Zuerst nur die Projektdateien kopieren und restoren -> stabiler Layer-Cache,
# solange sich die Abhaengigkeiten nicht aendern.
COPY src/Naudit.Core/Naudit.Core.csproj src/Naudit.Core/
COPY src/Naudit.Infrastructure/Naudit.Infrastructure.csproj src/Naudit.Infrastructure/
COPY src/Naudit.Web/Naudit.Web.csproj src/Naudit.Web/
RUN dotnet restore src/Naudit.Web/Naudit.Web.csproj

# Restlichen Quellcode kopieren und Release publishen (zieht Infrastructure+Core mit).
# ARG VERSION erst NACH dem COPY deklarieren: ein ARG invalidiert seinen eigenen und jeden
# nachfolgenden Layer, aber nicht die davor -- vor dem COPY stuende jede Versionsaenderung
# faelschlich auch den Quellcode-Copy-Layer neu.
COPY src/ src/
# VERSION reicht release.yml durch, damit der Startup-Report die echte Release-Version zeigt;
# lokal (Dockerfile-Default) bleibt "0.0.0-dev" -- der Startup-Report kennzeichnet nur ein
# "-dev"-Suffix (oder ein fehlendes Attribut) als (dev), niemals eine echte SemVer wie 1.0.0.
ARG VERSION=0.0.0-dev
RUN dotnet publish src/Naudit.Web/Naudit.Web.csproj -c Release -o /app/publish --no-restore /p:Version=${VERSION}

# --- Frontend-Build: SPA (Vite/React) fuer wwwroot ---
FROM node:26-alpine@sha256:725aeba2364a9b16beae49e180d83bd597dbd0b15c47f1f28875c290bfd255b9 AS frontend-build
WORKDIR /frontend
COPY src/frontend/package.json src/frontend/package-lock.json ./
RUN npm ci
COPY src/frontend/ ./
RUN npm run build

# --- Betterleaks aus Quelle bauen ---
# Zwei unabhaengige Gruende, das offizielle Release-Binary NICHT zu ziehen:
# 1. TOOLCHAIN: die betterleaks-Releases sind mit Go 1.25.10 gebaut und tragen dessen stdlib-CVEs
#    (CVE-2026-27145 crypto/x509 + CVE-2026-42504 net/textproto -> fixed in Go 1.25.11;
#    CVE-2026-39822 os.Root Symlink-Following/Directory-Traversal -> fixed in Go 1.25.12).
#    GOTOOLCHAIN=local zwingt den Build auf das Image-Go und ignoriert die (aeltere)
#    `toolchain`-Direktive in go.mod (sonst wuerde die alte Toolchain nachgeladen = wieder verwundbar).
# 2. DEPENDENCY: betterleaks pinnt bis einschliesslich v1.7.3 golang.org/x/text v0.38.0 und damit
#    CVE-2026-56852 (norm.Iter kann bei praeparierter Eingabe in eine Endlosschleife laufen -> DoS,
#    HIGH, fixed in 0.39.0). Ein Bump der betterleaks-Version behebt das NICHT -- der Build hebt die
#    indirekte Dependency daher selbst an (XTEXT_VERSION).
# Beides ist damit ECHT behoben (kein Suppress/keine VEX-Gate-Ausnahme noetig). Tag UND Digest des
# golang-Images mit jeder neuen stdlib-CVE nachziehen (Digest:
# `docker buildx imagetools inspect golang:1.25.x`), XTEXT_VERSION mit jeder neuen x/text-CVE --
# solange betterleaks keine Releases mit aktueller Toolchain und aktuellen Dependencies herausgibt.
# Der golang-Builder landet NICHT im finalen Image, das erzeugte Binary aber schon: die Modul-Integritaet
# garantiert die Go-Checksum-DB (go.sum / sum.golang.org), die Toolchain-/Base-Image-Integritaet der
# sha256-Digest-Pin (ein umgehaengter Tag koennte sonst die Go-Toolchain manipulieren).
FROM golang:1.25.12@sha256:d2e20dc1b35aefd666909163e4ace41efb521359aa2ce31fff59d86837050f6f AS betterleaks-build
ARG BETTERLEAKS_VERSION=1.7.3
ARG XTEXT_VERSION=0.39.0
# -mod=mod: der Build darf fehlende go.sum-Eintraege des Hilfsmoduls selbst nachtragen (die
# Pruefsummen kommen weiterhin aus der Checksum-DB -- das lockert die Integritaet nicht, nur die
# Buchfuehrung im Wegwerf-Modul). Auch die Aufloesung bleibt deterministisch: BEIDE Eingaben von
# MVS sind hier versionsgepinnt (BETTERLEAKS_VERSION + XTEXT_VERSION), der restliche Modulgraph
# steht in betterleaks' eigener go.mod. Gleiche ARGs -> gleicher Graph, ein committetes go.sum
# fuer das Wegwerf-Modul brauchte es nur fuer Offline-Builds -- die Stage laedt ohnehin ueber den
# Modul-Proxy, wie die Runtime-Stage ihre Tool-Binaries ueber curl.
ENV CGO_ENABLED=0 GOTOOLCHAIN=local GOFLAGS="-trimpath -mod=mod"
WORKDIR /build
# Umweg ueber ein Wegwerf-Hilfsmodul statt `go install pkg@version`: NUR im Modulkontext laesst sich
# eine indirekte Dependency anheben -- `go install pkg@version` baut strikt nach der go.mod des
# Zielmoduls und ignoriert jeden Override. Quelle bleibt in beiden Faellen der Modul-Proxy.
# `go mod edit -require` statt `go get`: reines Schreiben der beiden Requirements, ohne Netz und
# ohne die Sonderregeln, die `go get` auf ein Main-Package-Modul anwendet. Den Rest des Modulgraphen
# (inkl. go.sum, gegen die Checksum-DB geprueft) loest der Build dank -mod=mod selbst auf; x/text
# gewinnt per MVS, weil 0.39.0 > die 0.38.0 aus betterleaks' go.mod.
RUN go mod init naudit/betterleaks-build \
 && go mod edit -require="github.com/betterleaks/betterleaks@v${BETTERLEAKS_VERSION}" \
 && go mod edit -require="golang.org/x/text@v${XTEXT_VERSION}" \
 && go build -o /go/bin/betterleaks "github.com/betterleaks/betterleaks"
# Fail-closed: greift der Override nicht mehr (z. B. weil betterleaks x/text spaeter direkt pinnt),
# bricht der Image-Build hier ab -- statt das CVE still wieder ins Image zu lassen, wo es erst das
# Release-Gate (und damit jedes Release) reisst. `go version -m` liest die im Binary eingebettete
# Modulliste, also exakt die Quelle, aus der auch Trivy seine Funde ableitet.
# Bewusst EXAKTER Match auf XTEXT_VERSION, nicht ">= 0.39.0": ein hoeheres, ebenfalls sicheres
# x/text (weil betterleaks es spaeter selbst anhebt) laesst den Build damit auch scheitern. Das ist
# gewollt -- der Pin oben waere dann wirkungslos geworden, ohne dass es jemand merkt. Fix in dem
# Fall: XTEXT_VERSION auf die neue Version ziehen (oder, wenn betterleaks weit genug ist, den
# ganzen Override samt Hilfsmodul wieder ausbauen).
RUN go version -m /go/bin/betterleaks | grep -qE "golang\.org/x/text[[:space:]]+v${XTEXT_VERSION}"

# --- Runtime-Stage: schlankes ASP.NET-Image, non-root ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7 AS runtime
WORKDIR /app

# SAST/SCA/Secrets-Tools: Trivy + OpenGrep + OSV-Scanner als sha256-gepinnte Release-Binaries;
# Betterleaks wird oben aus Quelle gebaut und unten aus der Builder-Stage kopiert. Als root
# installieren, dann auf non-root wechseln. Versionen UND Regelset sind fest gepinnt
# (sha256-verifiziert) fuer Reproduzierbarkeit und Supply-Chain-Haertung. Kein Semgrep/pip mehr:
# spart Python im Image und vermeidet die lizenzbelastete Semgrep-Registry (`--config auto`).
ARG TRIVY_VERSION=0.72.0
ARG OPENGREP_VERSION=1.26.0
ARG OPENGREP_RULES_REF=f1d2b562b414783763fd02a6ed2736eaed622efa
ARG OSV_SCANNER_VERSION=2.4.0
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl \
 && curl -sfL -o /tmp/trivy.tar.gz "https://github.com/aquasecurity/trivy/releases/download/v${TRIVY_VERSION}/trivy_${TRIVY_VERSION}_Linux-64bit.tar.gz" \
 && echo "bbb64b9695866ce4a7a8f5c9592002c5961cab378577fa3f8a040df362b9b2ea  /tmp/trivy.tar.gz" | sha256sum -c - \
 && tar -xzf /tmp/trivy.tar.gz -C /usr/local/bin trivy \
 && rm /tmp/trivy.tar.gz \
 && curl -sfL -o /usr/local/bin/opengrep "https://github.com/opengrep/opengrep/releases/download/v${OPENGREP_VERSION}/opengrep_manylinux_x86" \
 && echo "40c21299eeddabf743b856daa843d24f9d4a027130671cd45b3b21776fd9ab26  /usr/local/bin/opengrep" | sha256sum -c - \
 && chmod +x /usr/local/bin/opengrep \
 && curl -sfL -o /tmp/opengrep-rules.tar.gz "https://github.com/opengrep/opengrep-rules/archive/${OPENGREP_RULES_REF}.tar.gz" \
 && echo "9a5f1cd5c625418cc1c776120123e2d4371df9bb66e099426b17c3488e13619d  /tmp/opengrep-rules.tar.gz" | sha256sum -c - \
 && mkdir -p /opt/opengrep-rules \
 && tar -xzf /tmp/opengrep-rules.tar.gz -C /opt/opengrep-rules --strip-components=1 \
 && rm /tmp/opengrep-rules.tar.gz \
 && rm -rf /opt/opengrep-rules/.github /opt/opengrep-rules/stats /opt/opengrep-rules/.pre-commit-config.yaml \
 && curl -sfL -o /usr/local/bin/osv-scanner "https://github.com/google/osv-scanner/releases/download/v${OSV_SCANNER_VERSION}/osv-scanner_linux_amd64" \
 && echo "15314940c10d26af9c6649f150b8a47c1262e8fc7e17b1d1029b0e479e8ed8a0  /usr/local/bin/osv-scanner" | sha256sum -c - \
 && chmod +x /usr/local/bin/osv-scanner \
 && apt-get purge -y curl && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/*

# Betterleaks aus der Builder-Stage (mit Go 1.25.11 gebaut, stdlib-CVEs behoben) ins Image.
COPY --from=betterleaks-build /go/bin/betterleaks /usr/local/bin/betterleaks

# Eigenes Regel-Overlay (.NET/C#-Security) ins Image (Pfad = Default in Naudit:Sast:OpengrepRules).
COPY sast/rules /opt/naudit-rules

# Claude Code CLI: Kernfunktion fuer den ClaudeCode-Provider und Autor-Sessions (Reviews ueber
# das Abo des MR-Autors). Native linux-x64-Binary (bringt eigene Node-Runtime mit), Version via
# stable-Zeiger aufgeloest und per manifest.json-Checksum verifiziert (fail-closed bei Mismatch).
# ARG = Pin/Notausgang fuer ein kaputtes CLI-Release (--build-arg CLAUDE_CODE_VERSION=x.y.z).
ARG CLAUDE_CODE_VERSION=
ADD https://downloads.claude.ai/claude-code-releases/stable /tmp/claude-stable
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends curl jq; \
    ver="${CLAUDE_CODE_VERSION:-$(cat /tmp/claude-stable)}"; \
    base="https://downloads.claude.ai/claude-code-releases/${ver}"; \
    sum="$(curl -fsSL "${base}/manifest.json" | jq -r '.platforms."linux-x64".checksum')"; \
    curl -fsSL -o /usr/local/bin/claude "${base}/linux-x64/claude"; \
    echo "${sum}  /usr/local/bin/claude" | sha256sum -c -; \
    chmod 755 /usr/local/bin/claude; \
    apt-get purge -y curl jq; \
    apt-get autoremove -y; \
    rm -rf /var/lib/apt/lists/* /tmp/claude-stable

# CLI-State braucht ein schreibbares HOME (non-root "app", 1654); Auto-Updater aus —
# wuerde als non-root nach /usr/local/bin schreiben wollen und scheitern.
ENV HOME=/home/app \
    DISABLE_AUTOUPDATER=1

COPY --from=build /app/publish .

# WebUI-SPA: DB+UI sind immer an, wwwroot wird also immer serviert.
COPY --from=frontend-build /frontend/dist ./wwwroot

# /data gehoert dem non-root-User: die DB ist Pflicht (DbSettingsLoader legt das
# Verzeichnis selbst an, aber "/" gehoert root -- ohne dieses chown scheitert das
# schon ohne gemountetes Volume mit UnauthorizedAccessException).
RUN mkdir -p /data && chown $APP_UID /data

# Vom Base-Image bereitgestellter non-root-User.
USER $APP_UID
EXPOSE 8080
# DB-Pflicht: im Container liegt die SQLite-Default-DB auf dem /data-Volume
# (der App-Default "data/naudit.db" ist fuer den Binary-Fall gedacht).
ENV Naudit__Db__ConnectionString="Data Source=/data/naudit.db"
ENTRYPOINT ["dotnet", "Naudit.Web.dll"]
