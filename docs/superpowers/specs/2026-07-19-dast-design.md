# Design: DAST (dynamische Sicherheitsprüfung an einer laufenden App)

*2026-07-19 · Projekt: Naudit*

## Ziel

Naudit soll ein Review nicht nur statisch (Diff + SAST/SCA + Repo-Kontext), sondern
auch **dynamisch** grounden: die im PR geänderte Anwendung wird **gebaut, gestartet
und im laufenden Zustand geprüft**. Ein LLM fährt dabei agentisch einen Browser
(Playwright) gegen die App und meldet Auffälligkeiten; diese Funde fließen wie
Semgrep/Trivy als Grounding in den Review-Prompt.

Das ist Slice B (App-Runner) + Slice C (dynamische Security) der DAST-Vision aus dem
Brainstorming 2026-07-11 — Slice A (MCP-Client + Tool-Loop) ist mit #54 bereits gebaut.
Leitsatz bleibt: **„Playwright ist die Hand, nicht das Hirn"** — die Navigation liefert
der Browser, die Vuln-Beurteilung das LLM.

## Abgrenzung & Nicht-Ziele

- **Verdict bleibt LLM-getrieben.** DAST ist ausschließlich Grounding (ein weiterer
  `ISastAnalyzer`). Es bestimmt nie das Merge-Gate — das leiten die LLM-Findings über
  das severity-aware Gate ab, wie bei jedem SAST-Fund. (Feste Repo-Regel.)
- **v1 ist an ein Dockerfile gekoppelt.** Nur Repos mit einem Dockerfile (Pfad
  konfigurierbar) werden dynamisch geprüft; keine Framework-/Build-Erkennung. Compose,
  mehrere Services, Auth-Seed-Daten, aktive Angriffs-Scans (ZAP full/Nuclei) sind
  bewusst spätere, additive Ausbauten an derselben Naht.
- **v1 ist für vertraute Projekte gedacht** (eigene Repos / `AccessGate:Registered`).
  Wir führen fremden PR-Code aus — das Risiko wird eingedämmt (siehe Sicherheit), aber
  nicht wegdefiniert. Im `Open`-Modus empfiehlt die Doku, DAST auszulassen.
- **Kein Verschleierungs-Zweck.** Isolation dient der RCE-Eindämmung, nicht dem
  Umgehen von Erkennung.

## Voraussetzungen (nicht Teil dieses Features)

- **`IDockerClient`/`SocketDockerClient` auf main.** Diese Docker-Socket-Naht existiert
  bereits gebaut+getestet auf `feat/session-sandbox` (Engine-API am Unix-Socket, kein
  neues NuGet, `DockerStreamDemux`, `FakeDockerClient`). **Entscheidung: Session-Sandbox
  zuerst mergen**, dann baut DAST auf der gemergten Naht auf und erweitert sie — keine
  Branch-Abhängigkeit, kein Duplikat.
- **MCP-E2E-Gate scharf.** Das LLM-Probing läuft über den Review-Tool-Loop aus #54;
  dessen manuelles E2E-Gate (ResponseFormat=Json + Tool-Loop koexistieren auf dem
  Zielmodell) muss vor Prod-Aktivierung erledigt sein. DAST erbt dieses Gate.

## Entscheidungen (Brainstorming 2026-07-19)

- **Build-Vertrag: Dockerfile** (nicht Compose) im Checkout.
- **Runtime: Host-Docker-Socket, Sibling-Container** — dieselbe `IDockerClient`-Naht wie
  die Session-Sandbox, kein Docker-in-Docker/K8s/VM.
- **Verfahren: LLM-gesteuertes Probing** über einen **Playwright-MCP-Server** im
  bestehenden Tool-Loop (#54) — keine eigene Browser-Steuerung, ein weiterer MCP-Server
  ist nur ein Config-Eintrag mehr. (Deterministischer Scanner ZAP/Nuclei bleibt als
  späterer zweiter `ISastAnalyzer` an derselben Naht offen.)
- **Timing: synchron im Review**, an der bestehenden SAST/SCA-Grounding-Phase
  eingehängt, **hartes Zeitbudget + fail-open**.
- **Eigener Kill-Switch:** `Naudit:Review:Dast:Enabled` (Default aus), **unabhängig** von
  `Naudit:Ai:SessionSandbox`. Ein Admin, der keinen `docker.sock` freigeben will, lässt
  beide aus; Sandbox und DAST sind getrennt schaltbar (unterschiedliche Risikoprofile:
  eigene Abo-Container vs. fremden PR-Code ausführen).
- **Fail-open über alles:** DAST an, aber Socket/Build/Healthcheck/Probing scheitert ⇒
  Warnung + leere Findings, Review läuft normal weiter (`ISastAnalyzer`-Vertrag).

Nachträge (Brainstorming 2026-07-24):

- **Erreichbarkeit ausschließlich über die Socket-Naht.** Naudit hängt sich nicht selbst
  ins Review-Netz und veröffentlicht keinen Port — auch nicht auf localhost. Der
  Healthcheck läuft als `docker exec` im Probe-Container (PR 1), das MCP-Gespräch mit
  Playwright als stdio über `docker exec` (PR 2; Exec-Streaming-Muster
  `DockerStreamDemux` der Session-Sandbox). Folge: identisches Verhalten, ob Naudit im
  Container oder als nackter Prozess läuft (Linux vorausgesetzt — der Client spricht
  nur Unix-Sockets), und das Review-Netz bleibt hermetisch.
- **Gemeinsames Container-Fundament, getrennte Policy-Besitzer.** Die Mechanik teilt
  sich DAST mit der Session-Sandbox: ein `IDockerClient`, die gehärtete
  `ContainerRunSpec`-Erweiterung (Limits/`cap-drop`/`no-new-privileges`), die
  Exec-Primitive (`ExecAsync`) und das Präfix-Sweep-Muster. Die Lebenszyklus-Besitzer
  bleiben bewusst getrennt (`SessionContainerManager` je Account, `IAppRunner` je
  Review): langlebig + Credential-Volume vs. kurzlebig + garantierter Teardown sind
  gegensätzliche Policies, und die Trennung stellt strukturell sicher, dass ein
  DAST-Container nie ein Volume, Env oder Secret bekommt.

## Phasing

Ein Spec, zwei PRs (das riskante Stück zuerst isoliert absichern):

- **PR 1 — App-Runner (ohne LLM verifizierbar):** `IDockerClient` um `BuildImageAsync`,
  Netz-Anlegen und Image-Pull erweitern; `IAppRunner` (build → run App + Probe-Container →
  Exec-Healthcheck → URL → garantierter Teardown). End-to-end testbar ohne jedes LLM —
  das RCE-Risiko (fremden Code ausführen + Sandbox-Härtung) steht isoliert und
  abgesichert.
- **PR 2 — Probing-Analyzer:** der Probe-Container aus PR 1 wird zum
  Playwright-MCP-Server im Tool-Loop (Transport: stdio über `docker exec` — der
  MCP-Connector aus #54 bekommt dafür einen Exec-Transport); `DastAnalyzer :
  ISastAnalyzer` orchestriert Runner + agentischen Lauf + Mapping auf `ScanFinding`;
  Registrierung als Analyzer (Config-only, bestehendes Erweiterungsmuster in
  `DependencyInjection.cs`).

## Architektur

### Container-Topologie (isoliert je Review-Id)

```
Docker-Netz  naudit-dast-net-<key>   (internal: true → KEIN Egress, KEINE veröffentlichten Ports)
 ├─ App-Container    naudit-dast-app-<key>   (aus dem PR-Dockerfile gebaut)
 └─ Probe/Playwright naudit-dast-pw-<key>    (ProbeImage; PR 1: Healthcheck-Arm, PR 2: MCP-Server)
        │  erreicht die App über das interne Netz: http://naudit-dast-app-<key>:<port>

Naudit ── docker exec über /var/run/docker.sock ──▶ Probe-Container
          (PR 1: Healthcheck-Kommando · PR 2: MCP über stdio)
```

Der App-Container hat **keinen** Egress; nur der Probe-/Playwright-Container teilt das
interne Netz mit ihm. Naudit selbst betritt das Netz **nie** — jede Interaktion läuft
als `docker exec` über die Socket-Naht (Entscheidung 2026-07-24). Die getestete App ist
für Naudit, den Host und das Internet unerreichbar; nur das Browser-Tool sieht sie.
Dadurch ist es auch egal, ob Naudit selbst als Container oder als nackter Prozess läuft.

### `IAppRunner` (Naudit.Infrastructure/Dast/)

```
RunAsync(IReviewWorkspace workspace, CancellationToken ct)
    -> RunningApp { InternalUrl, NetworkName, ContainerName, ProbeContainerName }  |  null
```

Nutzt `IDockerClient`:
1. Image aus `DockerfilePath` bauen (`BuildImageAsync`, neu).
2. Netz `naudit-dast-net-<key>` `internal: true` anlegen (neu).
3. App-Container an das Netz gehängt starten (Ressourcen-Limits, ohne Naudit-Secrets).
4. Probe-Container (`ProbeImage`, bei Bedarf gepullt) im selben Netz starten — PR 1s
   Healthcheck-Arm und PR 2s MCP-Server.
5. Healthcheck als `docker exec` im Probe-Container: HTTP-Probe auf
   `http://<app>:<AppPort><HealthPath>` bis Exit 0 oder Timeout.
6. `RunningApp` liefern — oder `null`, wenn der Healthcheck nie grün wird (App serviert
   kein Web ⇒ DAST no-op).

`IAsyncDisposable`: Teardown (beide Container, Netz, gebautes Image) **garantiert im
`finally`**, auch bei Exception/Cancel.

### `DastAnalyzer : ISastAnalyzer` (Naudit.Infrastructure/Dast/)

`AnalyzeAsync(workspace, changes)` orchestriert:
1. Kein Dockerfile am `DockerfilePath` ⇒ leere Liste (nicht anwendbar).
2. `await using var app = IAppRunner.RunAsync(...)`; `null` ⇒ leere Liste.
3. Agentischer Playwright-Lauf: eigener LLM-Call über den Tool-Loop (#54) mit den
   Playwright-MCP-Tools + einem Probing-System-Prompt (exploriere die App unter
   `app.InternalUrl`, melde Sicherheitsauffälligkeiten strukturiert). `MaxProbeSteps`
   deckelt den Loop (Token-frugal).
4. Roh-Funde → `ScanFinding(Tool: "dast", Category: Dast, Severity: …, Message)`.
   Die betroffene URL/der Endpoint steht im `Message` (kein neues Feld — `ScanFinding`
   hat nur `FilePath`/`Line`, die für einen dynamischen Fund `null` bleiben).
5. `finally`: Teardown über das `IAsyncDisposable` des Runners.

Registrierung: `case "dast"` in der Analyzer-Auswahl-Switch; aktiv nur, wenn `"dast"`
in `Naudit:Sast:Analyzers` **und** `Naudit:Review:Dast:Enabled=true`.

**Einzige Core-Änderung (additiv, kein Bruch):** `FindingCategory` (heute
`{ Sast, Sca, Secrets }`) bekommt einen Wert **`Dast`** — damit der Prompt DAST-Funde
als eigene Klasse ausweisen kann. Die `ISastAnalyzer`-Naht und die `ScanFinding`-Form
bleiben unverändert; die Core-Regel (Core hängt nur an MEAI-Abstraktionen) ist intakt.

### Datenfluss (in den bestehenden Review-Flow eingehängt)

```
ReviewService
  └─ SAST/SCA-Grounding-Phase (geteilter Checkout)
       └─ DastAnalyzer.AnalyzeAsync(workspace, changes)     [nur wenn Enabled + Dockerfile]
            ├─ IAppRunner.RunAsync → build/run/healthcheck   (Zeitbudget-gedeckelt)
            ├─ agentischer Playwright-Lauf (Tool-Loop #54)    → Roh-Funde
            ├─ map → ScanFinding[]
            └─ finally: Teardown (Container/Netz/Image weg)
  └─ ScanFindings fließen wie Semgrep/Trivy als Grounding in PromptBuilder
  └─ Verdict weiter aus den LLM-Findings über das Gate (DAST beeinflusst es nie)
```

DAST hängt an genau der Stelle, an der heute Semgrep/Trivy laufen (geteilter Checkout),
und verhält sich nach außen wie ein weiterer Analyzer.

## Sicherheit & Isolation

Wir bauen und starten fremden PR-Code — RCE by design. Eindämmung:

- **Kein Egress:** Review-Netz `internal: true` — App erreicht kein Internet, keinen
  fremden Container, nicht den Host. Nur der Playwright-Container teilt das Netz.
- **Ressourcen-Limits** an App- und Playwright-Container (`--memory`, `--cpus`,
  `--pids-limit`; konservative, konfigurierbare Defaults) gegen Fork-Bomb/OOM.
- **Keine Naudit-Secrets** im App-Env: nur was Dockerfile / eine optionale explizite
  Env-Liste vorgibt — nie `docker.sock`, nie Naudit-Tokens, nie die DB-Connection.
- **Kein Socket im App-Container:** `/var/run/docker.sock` ist nur an Naudit selbst
  gemountet, wird nie in getestete Container weitergereicht.
- **Keine veröffentlichten Ports:** weder App- noch Probe-Container mappen einen Port
  (auch nicht auf localhost) — Healthcheck und späteres MCP laufen als `docker exec`
  über die Socket-Naht.
- **Härtung best-effort:** `--security-opt no-new-privileges`, `--cap-drop ALL`,
  read-only wo möglich (minimal nachgeben, wenn ein Image Ports <1024 o. ä. braucht).
- **Harte Lebensdauer:** Zeitbudget killt hängende Container; Teardown im `finally`;
  **orphan-Sweeper** beim Naudit-Start entfernt `naudit-dast-*`-Container/Netze eines
  abgestürzten Vorlaufs (Muster wie Session-Sandbox-Adoption).
- **Doku ehrlich:** `docker.sock` ≈ Root auf dem Host; DAST nur für vertraute Projekte.

## Fehlerbehandlung & Zeitbudget

- **`Naudit:Review:Dast:TimeBudget`** (Default `00:05:00`) deckelt **build + run +
  probe** zusammen. Überschritten ⇒ Abbruch, Teardown, leere Findings, Review weiter.
- **Jeder Fehler fail-open:** Build scheitert / kein Dockerfile / Probe-Image nicht
  pullbar / Healthcheck-Timeout / Socket weg / Playwright-MCP startet nicht /
  LLM-Lauf wirft ⇒ geloggte Warnung + leere
  Liste. Nie ein Review-Abbruch.
- **Teardown garantiert** auch bei Exception/Cancel (`IAsyncDisposable` + `finally`).
- **Non-JSON aus dem Probing-Lauf** ⇒ „keine Funde" (Probing ist ein Grounding-Schritt,
  nicht der fail-closed Review-Final-Turn — anders als das MCP-Review-Gate).

## Konfiguration

Über den bestehenden `SettingsCatalog`/DB-Weg (list-förmige Keys env-only):

```
Naudit:Review:Dast:Enabled          = false             # eigener Kill-Switch, unabhängig von SessionSandbox
Naudit:Review:Dast:DockerfilePath   = "Dockerfile"      # relativ zum Checkout
Naudit:Review:Dast:AppPort          = 8080              # Port für Healthcheck/Browser-URL
Naudit:Review:Dast:HealthPath       = "/"               # HTTP-Pfad für den Healthcheck
Naudit:Review:Dast:TimeBudget       = 00:05:00
Naudit:Review:Dast:MemoryLimitMb    = 1024
Naudit:Review:Dast:MaxProbeSteps    = <n>               # Deckel für den agentischen Loop
Naudit:Review:Dast:DockerSocketPath = /var/run/docker.sock
Naudit:Review:Dast:ProbeImage       = mcr.microsoft.com/playwright/mcp:latest
                                                        # Probe-/MCP-Container; wird bei Bedarf gepullt
```

`"dast"` muss zusätzlich in `Naudit:Sast:Analyzers` stehen (Analyzer-Auswahl). `AppPort`/
`DockerfilePath` sind bewusst explizit statt Auto-Erkennung — passt zum lean-v1-Scope
(ein Web-Service, Dockerfile vorhanden).

## Testansatz

- **Kein echtes Docker im CI:** `FakeDockerClient` (aus der Session-Sandbox) erweitern;
  `IAppRunner`-Tests gegen build/run/healthcheck/teardown-Sequenz + **garantierten
  Teardown bei Exception**.
- **`DastAnalyzer`:** kein Dockerfile ⇒ leer; Healthcheck-Timeout ⇒ leer + Teardown;
  erfolgreicher Lauf ⇒ Roh-Funde → `ScanFinding`-Mapping; fail-open bei jedem Wurf.
  Fake-`IChatClient`/Fake-MCP-Connector wie in den bestehenden MCP-Tests.
- **Zeitbudget:** überschritten ⇒ Abbruch + leer.
- **`SocketDockerClient`-Erweiterung (BuildImage/Network):** opt-in Integrationstest
  gated auf `NAUDIT_TEST_DOCKER` (Muster wie `NAUDIT_TEST_POSTGRES`).
- **Manuelles E2E** (echte App + echtes Docker + Playwright-MCP) als Pflicht-Gate vor
  Prod-Aktivierung — wie beim MCP-Slice.

## Doku & Security

- **Neu `docs/dast.md`:** Zweck, Config-Keys, Topologie, Fail-Open, Betriebshinweise,
  Sicherheits-Trade-off (`docker.sock`, nur vertraute Projekte, `Open`-Modus meiden).
- **`docs/deployment.md`:** Socket-Mount + `group_add`/GID-Hinweis (geteilt mit der
  Session-Sandbox-Doku), DAST-Kill-Switch.

## Bewusst weggelassen (YAGNI / Ausblick)

- **Deterministischer Scanner** (ZAP-Baseline/Nuclei) als zweiter `ISastAnalyzer` —
  additiv an derselben Naht, später.
- **Aktive Angriffs-Scans** (ZAP full, Injection-Payloads), Auth/Seed-Daten,
  Multi-Service-Compose, Framework-Autoerkennung.
- **DAST-Funde mit Gate-Wirkung** — bleibt reines Grounding.
- **Zweiter Runtime-Ort/VM** — nur Host-Socket (wie Session-Sandbox).
