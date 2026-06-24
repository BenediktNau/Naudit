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

# --- Runtime-Stage: schlankes ASP.NET-Image, non-root ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:ddcf70ad1ab963a4fcd41fbd722a6b660e404e87567cfbd46fd2809c21b02088 AS runtime
WORKDIR /app

# SAST/SCA-Tools: Trivy (Binary) + Semgrep (pip). Als root installieren, dann auf non-root wechseln.
# Versionen sind fest gepinnt (sha256-verifiziert) fuer Reproduzierbarkeit und Supply-Chain-Haertung.
ARG TRIVY_VERSION=0.71.2
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl python3 python3-pip \
 && curl -sfL -o /tmp/trivy.tar.gz "https://github.com/aquasecurity/trivy/releases/download/v${TRIVY_VERSION}/trivy_${TRIVY_VERSION}_Linux-64bit.tar.gz" \
 && echo "0510e71e2fd39bf863856d499c8dc19feb4e7336546394c502a8f5cc7ab27460  /tmp/trivy.tar.gz" | sha256sum -c - \
 && tar -xzf /tmp/trivy.tar.gz -C /usr/local/bin trivy \
 && rm /tmp/trivy.tar.gz \
 && pip3 install --no-cache-dir --break-system-packages semgrep==1.167.0 \
 && apt-get purge -y curl && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Vom Base-Image bereitgestellter non-root-User.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Naudit.Web.dll"]
