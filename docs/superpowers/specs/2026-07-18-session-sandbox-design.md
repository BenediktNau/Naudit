# Design: Session-Sandbox (containerisierte Author/Round-Robin-Sessions)

*2026-07-18 · Projekt: Naudit*

## Ziel

Die Claude-Abo-Sessions (Author- und Round-Robin-Modus) laufen heute als
In-Process-Subprozesse: pro Review startet der `SystemProcessRunner` die `claude`-CLI
mit einem **frischen, leeren `CLAUDE_CONFIG_DIR`** (GUID-Temp-Dir) — jeder Request
baut die CLI-Auth also kalt neu auf ("meldet sich immer neu an").

Dieses Feature lagert die Abo-Sessions in **Geschwister-Container pro Account** aus,
gestartet über den **Host-Docker-Socket**. Zwei Effekte:

1. **Warme Session pro Account** — Prozess *und* Auth bleiben zwischen Reviews warm
   (langlebiger Container + persistentes Volume), kein Kaltstart/Neu-Auth je Review.
2. **Isolation** — jeder Account läuft in einem eigenen Container mit eigenem Volume;
   ein Lauf sieht fremde Tokens/State nie.

Rein **additiv**: Default bleibt exakt das heutige In-Process-Verhalten.

## Abgrenzung (ausdrücklich)

Das Feature dient **Isolation und Performance**, **nicht** der Verschleierung
gegenüber Anthropic. Der ToS-Status des Round-Robin-Poolings (Account-Sharing über
Consumer-Abos, siehe `2026-07-15-round-robin-sessions-design.md`) ändert sich dadurch
**nicht**. Es werden keine Mechanismen gebaut, die die Erkennung von ToS-Verstößen
umgehen sollen.

## Entscheidungen (Brainstorming 2026-07-18)

- **Primärziel: warme Session (Perf).** Kein Neu-Auth pro Review.
- **Runtime: Host-Docker-Socket, Sibling-Container, selber Host.** Naudit bekommt
  `/var/run/docker.sock` gemountet und startet Geschwister-Container. (Kein
  Docker-in-Docker, kein K8s, keine VM — bewusst der schlankste Weg fürs
  Single-Host-Coolify-Deploy.) Sicherheits-Trade-off `docker.sock` ≈ Root auf dem
  Host ist bekannt und wird dokumentiert.
- **Kein zweites Image.** Der Account-Container ist **dasselbe Naudit-Image** mit
  `sleep infinity` als Kommando; die `claude`-CLI liegt darin bereits
  (Dockerfile Z. 86–103). Das eigene Image-Ref wird zur Laufzeit per
  `docker inspect $HOSTNAME` (`.Image`) ermittelt. CLI-Version driftet nie.
- **Lifecycle: langlebig + `docker exec`.** Ein dauerhaft laufender Container pro
  Account; Reviews werden per `docker exec` hineingereicht → Prozess *und* Auth warm.
- **Idle-Grenze: stop, nicht rm.** Variante (a): Idle-Timeout **stoppt** den Container
  (exited, kostet ~nichts), Restart via `docker start`. `docker rm` passiert erst bei
  **Pool-Austritt/Account-Löschung**. Die warme Session hängt am **Volume**, das stop
  *und* rm überlebt.
- **Idle-Timeout Default = 2 Tage** (`2.00:00:00`). Bewusst lang, damit Container über
  eine Arbeitswoche warm bleiben. Konsequenz: nicht der Sweeper, sondern
  **`MaxLiveContainers` (LRU-Stopp) ist der eigentliche Ressourcen-Begrenzer** im
  Alltag; der Sweeper ist das Netz für „Account 2 Tage still" (Wochenende/Urlaub).
- **v1-Scope:** Idle-Sweeper, `MaxLiveContainers`-Cap, WebUI-Sandbox-Status.
  **Verschoben:** MCP-Tool-Loop im Container (Sessions laufen in v1 ohne MCP, wie der
  einfache Review-Pfad).
- **Docker-Zugang: eigener Mini-Client über den Unix-Socket** (kein neues NuGet), nicht
  `Docker.DotNet` — passt zur Supply-Chain-Härtung (Trivy-Gate, gepinnte Deps).
- **Fail-Open über alles.** Mode `Docker`, aber Socket nicht erreichbar/nutzbar ⇒
  stiller Fallback auf den heutigen In-Process-Runner. Ein Review scheitert **nie an
  der Sandbox**.

## Architektur

Baut auf der bestehenden Session-Maschinerie (`SessionSelectionFactory`,
`ClaudeCodeChatClient`, `IProcessRunner`) auf. Neu sind ein account-gebundener
Container-Runner, ein Lifecycle-Manager und ein dünner Docker-Client. SAST/git bleiben
unangetastet am `SystemProcessRunner`.

### Konfiguration

Neuer Modus + Sub-Optionen in `AiOptions` (Naudit.Infrastructure), über den
bestehenden Settings-Katalog/DB-Weg (`SettingsCatalog.cs`):

- **`Naudit:Ai:SessionSandbox = None | Docker`** (Default `None` = heutiges
  In-Process-Verhalten). Orthogonal zu `SessionRouting` — die Sandbox greift für den
  `Author`- **und** `RoundRobin`-Modus; im `Single`-Modus (globaler Provider) ist sie
  bedeutungslos.
- **`Naudit:Ai:Sandbox:IdleTimeout`** (`TimeSpan`, Default `2.00:00:00`)
- **`Naudit:Ai:Sandbox:MaxLiveContainers`** (`int`, Default `5`)
- **`Naudit:Ai:Sandbox:DockerSocketPath`** (Default `/var/run/docker.sock`)
- **`Naudit:Ai:Sandbox:Image`** (optionaler Override; leer ⇒ Selbst-Inspektion)

### `IDockerClient` — dünne Naht über die Engine-API

Interface mit nur den benötigten Operationen (Naudit.Infrastructure/Docker/):

```
PingAsync()                       // Socket erreichbar + nutzbar?
InspectSelfImageAsync(hostname)   // eigenes Image-Ref
InspectContainerAsync(name)       // existiert? läuft? -> State
RunDetachedAsync(spec)            // docker run -d (name, image, volume, env, cmd)
StartAsync(name)
StopAsync(name)
RemoveAsync(name)
ExecAsync(name, argv, stdin, env, timeout, ct) -> (exitCode, stdout, stderr)
ListNaudit SessionContainersAsync() // Adoption nach Neustart (Name-Präfix)
```

Default-Impl `SocketDockerClient`: HTTP über die Docker-Engine-API am Unix-Socket via
`SocketsHttpHandler { ConnectCallback = <UnixDomainSocketEndPoint> }`. Kein neues
NuGet. `ExecAsync` = Docker-`exec`-Create + `exec`-Start (hijacked stream) für
stdout/stderr; Timeout/Cancel killt den Exec bzw. wird über den `IProcessRunner`-Kill-
Pfad gespiegelt.

### `SessionContainerManager` — Lifecycle

Prozessweiter Singleton, hält den Zustand aller Account-Container:

- **`EnsureRunningAsync(accountId)`** → Containername `naudit-session-<accountId>`.
  `inspect`: fehlt ⇒ `RunDetachedAsync` (Image = Selbst-Inspektion, Volume
  `naudit-session-<accountId>` → `HOME`, `sleep infinity`); exited ⇒ `StartAsync`;
  läuft ⇒ nichts. Vor dem Start ggf. `MaxLiveContainers` erzwingen (LRU-Stopp des am
  längsten ungenutzten laufenden Containers). Gibt den Containernamen zurück.
- **Lock pro Account** (`SemaphoreSlim` je `accountId`): nie zwei `exec` gleichzeitig
  in denselben Container (Race auf den Credential-Cache im geteilten Volume). Passt zu
  Round-Robin = sequenziell.
- **`LastUsed`-Tracking** je Account (in-memory), gesetzt nach jedem Exec.
- **Adoption nach Naudit-Neustart:** beim Start `ListNauditSessionContainersAsync()`
  → bestehende Container per Name wieder übernehmen (der Restart-Loop in `Program.cs`
  betrifft die Geschwister-Container nicht).
- **`RemoveAsync(accountId)`**: `stop+rm` (+ Volume optional) bei Pool-Austritt /
  Account-Löschung.

### Idle-Sweeper (`IHostedService`)

Periodischer Timer (z. B. alle 5 min): stoppt laufende Account-Container, deren
`LastUsed` älter als `IdleTimeout` ist (`StopAsync`, **kein** rm). Fail-quiet.

### `DockerSessionRunner : IProcessRunner` — account-gebunden

`SessionSelectionFactory.ForAccount(accountId, token)` injiziert für den Sandbox-Modus
einen **account-gebundenen** `DockerSessionRunner` in den `ClaudeCodeChatClient` (statt
des geteilten `SystemProcessRunner`). Der Runner:

1. `manager.EnsureRunningAsync(accountId)` + Account-Lock nehmen,
2. den `ProcessSpec` in `docker exec <container> <FileName> <Args…>` übersetzen, stdin
   durchgereicht,
3. **Env-Filter:** nur `CLAUDE_CODE_OAUTH_TOKEN` weiterleiten, **`CLAUDE_CONFIG_DIR`
   verwerfen** — das Container-`HOME` (= Volume) gewinnt, damit die Session warm bleibt,
4. `ExitCode/StdOut/StdErr` als `ProcessResult` zurückgeben (Contract unverändert),
5. Exec schlägt fehl, weil Container weg ⇒ einmal `EnsureRunning` + Retry.

`ClaudeCodeChatClient` bleibt der Arg-/Prompt-Builder und braucht **keine** Änderung:
das host-seitige `CLAUDE_CONFIG_DIR`-Temp-Dir, das er anlegt, wird im Container-Fall
schlicht nicht benutzt (leer angelegt + im `finally` wieder gelöscht — harmlos). MCP
bleibt in v1 außen vor (Sandbox-Runner ohne MCP-Config).

### Fail-Open / Auto-Detect

`AddNauditInfrastructure`: bei `SessionSandbox=Docker` einmalig `PingAsync()`. Nicht
erreichbar/nutzbar ⇒ Warnung loggen und `SessionSelectionFactory` nutzt weiter den
`SystemProcessRunner` (heutiger Pfad). Zur Laufzeit auftretende Docker-Fehler im Runner
fallen ebenfalls auf den In-Process-Lauf zurück, bevor sie ein Review kippen.

Non-root-Zugriff (`$APP_UID`) auf den Socket: `group_add: [<docker-gid>]` im
Compose/Coolify dokumentieren; zusätzlich versucht Naudit die Socket-GID zur Laufzeit
zu erkennen und sich als supplementary group zu ergänzen (best-effort).

### WebUI-Sandbox-Status

Endpoint (z. B. `GET /api/me/session-sandbox`, nur gemappt wenn `SessionSandbox=Docker`):
`{ mode, socketReachable, liveContainers }`. Die SPA zeigt eine Statuszeile
(„Sandbox aktiv / Fallback In-Process / docker.sock fehlt"). Optional ein Live-Test im
Setup-Wizard (`PingAsync` → grün/rot).

## Datenfluss (Sandbox-Modus, Round-Robin/Author)

```
ReviewService
  └─ IAiClientRouter.SelectAsync            (RoundRobin/AuthorSessionRouter, unverändert)
       └─ SessionSelectionFactory.ForAccount(accountId, token)
            └─ ClaudeCodeChatClient(opts+token, DockerSessionRunner(accountId))
                 └─ RunAsync(spec)
                      ├─ SessionContainerManager.EnsureRunningAsync(accountId)  (run/start, LRU-Cap)
                      ├─ Account-Lock
                      ├─ IDockerClient.ExecAsync("naudit-session-<id>", ["claude", …], stdin=diff)
                      └─ LastUsed = now
Idle-Sweeper (IHostedService)  ── periodisch ──▶  StopAsync(idle > IdleTimeout)
Opt-out / Account-Delete       ──────────────▶  RemoveAsync(accountId)  (stop+rm)
```

## Fehlerbehandlung

- **Socket nicht da / nicht nutzbar:** Fallback In-Process (Start-Probe + Laufzeit).
- **Container-Start scheitert:** einmal Retry, sonst Fallback In-Process für dieses
  Review.
- **Exec-Timeout/Cancel:** wie beim `SystemProcessRunner` — Exec killen, `TimeoutException`.
- **`MaxLiveContainers` erreicht:** LRU-Stopp vor dem nächsten Start (kein Abbruch).
- **Adoption findet fremd benannte Container:** ignorieren (nur `naudit-session-`-Präfix).

## Testansatz

Kein echtes Docker im CI — `IDockerClient` wird gefaked (`FakeDockerClient`):

- **`SessionContainerManager`:** `EnsureRunning`-Idempotenz (run vs. start vs. noop),
  LRU-Cap (ältester wird gestoppt), Account-Lock (serialisiert), Adoption.
- **Idle-Sweeper:** stoppt nur `LastUsed > IdleTimeout`, lässt frische in Ruhe.
- **`DockerSessionRunner`:** Spec→`exec`-Übersetzung, Env-Filter (`CLAUDE_CONFIG_DIR`
  raus, Token rein), Retry-nach-Container-weg, Fail-Open.
- **`SocketDockerClient`:** opt-in Integrationstest gegen echtes Docker, gated auf
  `NAUDIT_TEST_DOCKER` (Muster wie `NauditDbContextPostgresTests`/`NAUDIT_TEST_POSTGRES`).

## Doku & Security

- **Neu `docs/session-sandbox.md`:** Zweck, Config-Keys, Lifecycle, Fail-Open,
  Betriebshinweise (Idle/Cap-Zusammenspiel).
- **`docs/deployment.md`:** Socket-Mount + `group_add`-Beispiel, GID-Hinweis.
- **Security-Notiz (deutlich):** `docker.sock` ≈ Root auf dem Host; das Abo-Token liegt
  in Container-Env und -Volume (bei Socket-Zugriff ohnehin kompromittierbar — ehrlich
  benennen). Volume `naudit-session-<id>` enthält CLI-Credentials und überlebt bewusst
  Container-Restarts.

## Bewusst weggelassen (YAGNI / Ausblick)

- **MCP-Tool-Loop im Container** (mcp-config + Secrets ins Volume/Container) —
  verschoben; v1-Sessions ohne MCP.
- **Docker-in-Docker / rootless Podman / K8s / VM-pro-Account** — nur Host-Socket.
- **Quota-/lastgewichtetes Container-Scheduling** — LRU + fester Cap reichen.
- **Persistenter Lifecycle-State** (DB) — in-memory `LastUsed`/Locks; Adoption über
  Container-Namen reicht.
