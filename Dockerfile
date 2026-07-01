# syntax=docker/dockerfile:1

# --- Build-Stage: SDK kompiliert und published das Web-Projekt ---
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:548d93f8a18a1acbe6cc127bc4f47281430d34a9e35c18afa80a8d6741c2adc3 AS build
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

# --- Betterleaks aus Quelle bauen ---
# Das offizielle betterleaks-1.6.1-Release-Binary ist mit Go 1.25.10 gebaut und traegt zwei
# Go-stdlib-DoS-CVEs (CVE-2026-27145 crypto/x509, CVE-2026-42504 net/textproto), fixed erst in
# Go 1.25.11 -- ein neueres betterleaks-Release existiert (Stand 2026-07-01) noch nicht. Wir bauen
# das versionsgepinnte Modul @v${BETTERLEAKS_VERSION} daher selbst mit Go 1.25.11 -> die stdlib-CVEs
# sind damit ECHT behoben (kein Suppress/keine VEX-Gate-Ausnahme noetig).
# GOTOOLCHAIN=local zwingt den Build auf das Image-Go 1.25.11 und ignoriert die
# `toolchain go1.25.10`-Direktive in go.mod (sonst wuerde 1.25.10 nachgeladen = wieder verwundbar).
# Der golang-Builder landet NICHT im finalen Image; die Modul-Integritaet garantiert die
# Go-Checksum-DB (go.sum / sum.golang.org).
FROM golang:1.25.11 AS betterleaks-build
ARG BETTERLEAKS_VERSION=1.6.1
ENV CGO_ENABLED=0 GOTOOLCHAIN=local GOFLAGS=-trimpath
RUN go install "github.com/betterleaks/betterleaks@v${BETTERLEAKS_VERSION}"

# --- Runtime-Stage: schlankes ASP.NET-Image, non-root ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:ddcf70ad1ab963a4fcd41fbd722a6b660e404e87567cfbd46fd2809c21b02088 AS runtime
WORKDIR /app

# SAST/SCA/Secrets-Tools: Trivy + OpenGrep + OSV-Scanner als sha256-gepinnte Release-Binaries;
# Betterleaks wird oben aus Quelle gebaut und unten aus der Builder-Stage kopiert. Als root
# installieren, dann auf non-root wechseln. Versionen UND Regelset sind fest gepinnt
# (sha256-verifiziert) fuer Reproduzierbarkeit und Supply-Chain-Haertung. Kein Semgrep/pip mehr:
# spart Python im Image und vermeidet die lizenzbelastete Semgrep-Registry (`--config auto`).
ARG TRIVY_VERSION=0.72.0
ARG OPENGREP_VERSION=1.23.0
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
 && echo "1f06548af379ab6080698a609612890ffad2d92dc2172f1e97d38d48096d5ef8  /usr/local/bin/opengrep" | sha256sum -c - \
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

COPY --from=build /app/publish .

# Vom Base-Image bereitgestellter non-root-User.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Naudit.Web.dll"]
