# syntax=docker/dockerfile:1

# Abgeleitetes Image NUR fuer die eigene Coolify-Instanz: das oeffentliche Naudit-Image
# plus die `claude` CLI, damit der ClaudeCode-Provider (ClaudeCodeChatClient ruft `claude`
# als lokalen Prozess auf) funktioniert. Das Haupt-Dockerfile / Public-Image bleibt unangetastet.
# Auth laeuft ueber CLAUDE_CODE_OAUTH_TOKEN (Abo, kein API-Key) als Coolify-Env-Var.
# Details: docs/deployment.md ("Claude Code CLI provider").

# Rolling :latest -> ein Coolify-Rebuild zieht immer das neueste Naudit-Release als Basis.
FROM ghcr.io/benediktnau/naudit:latest

# Optionaler Pin/Notausgang: leer = neueste stable-Version beim Build; gesetzt (z.B.
# --build-arg CLAUDE_CODE_VERSION=2.1.193) = exakt diese Version. Damit kann ein kaputtes
# Claude-Code-Release zurueckgerollt werden, ohne das Default-Verhalten (immer latest) zu verlieren.
ARG CLAUDE_CODE_VERSION=

# Der `stable`-Zeiger (winziger Versionsstring) wird von BuildKit bei jedem Build geladen und
# bustet den Layer-Cache automatisch, sobald eine neue Version erscheint -> kein 250-MB-Redownload,
# solange sich nichts geaendert hat.
ADD https://downloads.claude.ai/claude-code-releases/stable /tmp/claude-stable

# curl + jq nur temporaer zum Aufloesen/Verifizieren; im selben Layer wieder entfernt.
# Die SHA256 kommt aus der manifest.json der Version und wird per sha256sum geprueft
# -> fail-closed: bei Mismatch bricht der Build ab. Checksum-only: Anthropic GPG-signiert die
# manifest.json (manifest.json.sig), diese Signatur wird hier bewusst NICHT geprueft -> schuetzt
# gegen korrupten/abgebrochenen Download, nicht gegen kompromittierten Upstream. Die
# selbstständige native linux-x64-Binary bringt eigene Node-Runtime + ripgrep mit.
USER root
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

# Der non-root User $APP_UID (1654, "app") braucht ein schreibbares HOME fuer den
# CLI-State (~/.claude, ~/.claude.json); /home/app existiert im Basis-Image und gehoert ihm.
# Auto-Updater aus: wuerde als non-root nach /usr/local/bin schreiben wollen und scheitern.
ENV HOME=/home/app \
    DISABLE_AUTOUPDATER=1

# Zurueck auf non-root; ENTRYPOINT ["dotnet", "Naudit.Web.dll"] wird vom Basis-Image geerbt.
USER $APP_UID
