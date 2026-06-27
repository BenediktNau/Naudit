# Design: SAST-Ausbau — OpenGrep-Engine, LLM-Verifikation & weitere Sensoren

*2026-06-27 · Projekt: Naudit*

## Ziel

Das SAST/SCA-Grounding (`2026-06-24-sast-review-context-design.md`) steht: Naudit klont den
MR/PR-Head, lässt Scanner laufen und gibt die Funde als Grounding ins LLM. Diese Roadmap macht
daraus einen **produktiv tragfähigen** Stand statt eines POC — entlang von vier Entscheidungen,
die wir gemeinsam festgehalten haben:

1. **OpenGrep als SAST-Engine festnageln** (Semgrep raus). Voll-LGPL-2.1, keine Telemetrie, volle
   Features, immun gegen weitere Semgrep-Lizenzverschärfungen.
2. **Regelset extern + gepinnt + kleines eigenes Overlay** — nicht selbst ein breites Set pflegen.
3. **LLM-Verifikations-Reducer** als strategischen Hebel: füllt den bereits vorhandenen
   `IFindingReducer`-Seam (`Reducer = "llm"`) und macht aus Tool-Breite Signal statt Spam.
4. **Breite über weitere Sensoren** inkrementell: Gitleaks (Secrets), OSV-Scanner (SCA),
   Hadolint + actionlint + zizmor (Infra-Härtung) — je ein eigener fokussierter PR.

**Kernprinzip bleibt:** Tools sind Sensorik (Recall), das LLM ist Triage/Erklärung/Verifikation
(Precision). Das Verdict trifft weiterhin **allein das LLM**; kein hartes Tool-Gate. Die
Core-Regel (`Core → MEAI-Abstractions only`) und das Plugin-Muster (`ISastAnalyzer`) bleiben
unangetastet — jedes neue Tool = eine Infra-Klasse + Config-Eintrag + Dockerfile-Install.

## Strategische Entscheidungen

- **OpenGrep ersetzt Semgrep, kein Parallelbetrieb.** OpenGrep ist drop-in (gleiche Regel-Syntax,
  gleicher JSON/SARIF-Output), die Analyzer-Logik (JSON-Parsing, Severity-Mapping) bleibt praktisch
  identisch. Zwei Engines nebeneinander wären unnötiger Code/Test-Ballast (YAGNI).
- **Regeln kommen aus `opengrep/opengrep-rules` (LGPL-2.1), per Commit gepinnt** und im Docker-Build
  ins Image gelegt — **kein `--config auto`** (Registry-Lizenz + Telemetrie + Nicht-Reproduzierbarkeit
  entfallen damit alle drei). Dazu ein **kleines eigenes Overlay** im Repo (`sast/rules/`, ~10–15
  kuratierte C#/.NET-Regeln) für hauseigene Konventionen. Aktualisierung des externen Sets via
  Dependabot/Renovate-pinbarem Commit, nicht „live".
- **Regel-Scope kuratiert, nicht „alles".** `opengrep-rules` enthält tausende Regeln über viele
  Sprachen. Naudit zeigt OpenGrep nur die relevanten Teilbäume (C#/.NET, generische Security,
  Dockerfile) + das Overlay — hält Latenz und Rauschen niedrig.
- **Neue `FindingCategory.Secrets`** für Gitleaks. Heute nur `{ Sast, Sca }`. Secrets sind weder
  noch; eine eigene Kategorie hält die Grounding-Sektion und die Verdichtung sauber gruppiert. Das
  ist die **einzige Core-Model-Änderung** der gesamten Roadmap.
- **LLM-Reducer verifiziert/filtert, erfindet nichts.** Sein Output ist eine **Teilmenge** des
  Inputs (Funde werden bestätigt, verworfen oder in der Message präzisiert — nie neu erzeugt). Damit
  kein Recall-Risiko durch Halluzination, und der `IFindingReducer`-Kontrakt (`ScanFinding[]`) bleibt
  exakt gleich. Default bleibt **deterministisch** (reproduzierbar für die BA-Messung); `"llm"` ist
  opt-in.
- **Tool-Installation nach bestehendem Dockerfile-Muster:** Binary von der Release-Page, **Version
  gepinnt + sha256-Prüfung** (genau wie Trivy heute). Gilt für opengrep, gitleaks, osv-scanner,
  hadolint, actionlint, zizmor. Python/`pip`-Semgrep entfällt.
- **Graceful degradation unverändert:** ein Analyzer-Fehler/Timeout ⇒ Warning + skip; alle Tools
  rein statisch (führen Fremdcode nicht aus); `dotnet-sca` bleibt das einzige opt-in mit Build.

## Sequenz (je ein fokussierter PR)

| PR | Task | Core-Änderung | Größe |
|----|------|---------------|-------|
| **1** | **OpenGrep ersetzt Semgrep** (Engine + gepinntes Regelset + Overlay + Dockerfile) | nein | M |
| **2** | **Gitleaks** (Secrets) | **ja** (`FindingCategory.Secrets`) | S |
| **3** | **OSV-Scanner** (SCA) | nein | S |
| **4** | **Infra-Linter-Bündel** (Hadolint + actionlint + zizmor) | nein | M |
| **5** | **LLM-Verifikations-Reducer** (`Reducer="llm"`, strategischer Hebel) | nein | L |

Reihenfolge im Spec-Review entschieden: erst die Engine sauber stellen (PR 1), dann **Breite über
die Sensoren** (PR 2–4, voneinander unabhängig, umordnbar), und **zuletzt der LLM-Verifikations-
Reducer** (PR 5) — so hat er beim Bau bereits das volle Tool-Rauschen aller Sensoren zum Filtern.

**Zurückgestellt (Non-Goal dieser Roadmap):** **ast-grep** — bringt ohne handgeschriebene
Strukturregeln wenig über OpenGrep hinaus; eigener Spec, falls später konkret motiviert. Ebenso
weiterhin: hartes Tool-Gate, WebSearch/Aktualitätsrecherche.

---

## PR 1 — OpenGrep ersetzt Semgrep (detailliert)

Das einzige Stück, das hier vollständig durchdesignt wird; PR 2–5 sind darunter umrissen und
bekommen je einen eigenen Spec, wenn sie dran sind.

### Komponenten

- **`Infrastructure/Sast/OpengrepAnalyzer`** (ersetzt `SemgrepAnalyzer`) — Aufruf
  `opengrep scan --config <p1> [--config <p2> …] --json .` (WorkingDirectory = Workspace-Root).
  **Kein `--metrics`-Flag** (OpenGrep hat keine Telemetrie/Registry; das Flag existiert nicht und
  würde fehlschlagen) und **nie `--config auto`**. Am echten Binary v1.23.0 verifiziert: Flags
  `--config`/`--json` existieren, JSON-Output ist Semgrep-kompatibel → `…Report`-Parsing und
  Severity-Mapping (ERROR→High, WARNING→Medium, INFO→Low) übernommen. `Name => "opengrep"`,
  `Category.Sast`. Fehler/Exit>1 ⇒ leere Liste (geloggt), wie heute.
- **`SemgrepAnalyzer` + `SemgrepAnalyzerTests` entfallen** (harter Schnitt; `case "semgrep"` wird zum
  unbekannten Namen → wirft wie gehabt). `OpengrepAnalyzerTests` prüfen Mapping **und** die exakten
  Argumente (`scan … --config … --json .`, niemals `auto`).
- **Regelquelle (beide gepinnt):**
  - extern: `opengrep/opengrep-rules` (LGPL-2.1) per **Commit** `f1d2b562…` als sha256-verifizierter
    Tarball nach `/opt/opengrep-rules`. Config zeigt auf **kuratierte Teilbäume** (`csharp`,
    `generic`, `dockerfile`) — **nicht** den vollen Baum: eine einzige ungültige Regel darin
    (`InvalidRuleSchemaError`) bricht den **ganzen** Scan ab (am Binary verifiziert). Pro Deployment
    via Config um weitere Sprach-Teilbäume erweiterbar.
  - Overlay: `sast/rules/dotnet-security.yaml` im Naudit-Repo (6 hochsignalige .NET-Regeln:
    schwache Krypto MD5/SHA1, ECB-Modus, Command-Injection `Process.Start`-Concat, EF-Raw-SQL-
    Interpolation, `BinaryFormatter`, deaktivierte TLS-Prüfung) → `/opt/naudit-rules`. Gegen das
    echte Binary validiert (lädt fehlerfrei, matcht alle Zielstellen).
- **`DependencyInjection.cs`:** `case "semgrep"` → `case "opengrep"`; Default-Fallback-Liste
  `{ "semgrep", "trivy" }` → `{ "opengrep", "trivy" }`; Default-`OpengrepRules` = die kuratierten
  Pfade + Overlay (gesetzt wenn Config leer, analog zur Analyzer-Liste).
- **`SastOptions`:** neue Liste `OpengrepRules` (je Eintrag ein `--config`-Pfad), config-
  überschreibbar; leer ⇒ Default in DI.

### Dockerfile

`pip install semgrep==…` und `python3`/`pip` entfallen (kleineres Image, weniger CVE-Oberfläche).
OpenGrep ist ein self-contained Binary (`opengrep_manylinux_x86`, glibc → passt zum aspnet-Image).
Muster wie der bestehende Trivy-Block (`ARG …_VERSION`, `curl`, `sha256sum -c`), real gepinnt:

```dockerfile
ARG OPENGREP_VERSION=1.23.0
ARG OPENGREP_RULES_REF=f1d2b562b414783763fd02a6ed2736eaed622efa
RUN curl -sfL -o /usr/local/bin/opengrep ".../v${OPENGREP_VERSION}/opengrep_manylinux_x86" \
 && echo "1f06548a…5ef8  /usr/local/bin/opengrep" | sha256sum -c - && chmod +x /usr/local/bin/opengrep \
 && curl -sfL -o /tmp/r.tar.gz ".../opengrep-rules/archive/${OPENGREP_RULES_REF}.tar.gz" \
 && echo "9a5f1cd5…619d  /tmp/r.tar.gz" | sha256sum -c - \
 && mkdir -p /opt/opengrep-rules && tar -xzf /tmp/r.tar.gz -C /opt/opengrep-rules --strip-components=1
COPY sast/rules /opt/naudit-rules
```

### Config

```
Naudit:Sast:Analyzers     = ["opengrep","trivy"]    # dotnet-sca weiterhin opt-in
Naudit:Sast:OpengrepRules = ["/opt/opengrep-rules/csharp","/opt/opengrep-rules/generic",
                             "/opt/opengrep-rules/dockerfile","/opt/naudit-rules"]   # Default; überschreibbar
```

### Tests (TDD)

- **`OpengrepAnalyzerTests`** (3 Fälle) — `StubProcessRunner` füttert echten OpenGrep-`--json`-Output
  ⇒ korrektes `ScanFinding`-Mapping (WARNING→Medium etc.); ein Fall pinnt die **exakten Argumente**
  (`scan --config … --json .`, enthält **nie** `auto`); ein Fall: Exit>1 ⇒ leere Liste.
- **`SastWiringTests`** — `"opengrep"` registriert den `OpengrepAnalyzer`, Default ist
  `opengrep`+`trivy`; `"semgrep"` ist kein gültiger Name mehr (harter Schnitt, unbekannter Name wirft).
- Bestehende `ReviewServiceTests`/`PromptBuilderTests`/`DeterministicFindingReducerTests` laufen
  weiter (Tool-Label-Fixtures `semgrep`→`opengrep` angeglichen).

### Doku

- `docs/`-SAST-Abschnitt: Semgrep → OpenGrep (Tool im Container, Regelquelle gepinnt, kein
  `--config auto`, Lizenz-Begründung in einem Satz).
- `CLAUDE.md`: Extension-Point-/Tool-Liste auf `opengrep` aktualisieren.

---

## PR 2 — Gitleaks / Secrets (Umriss)

- **Core:** `FindingCategory.Secrets` ergänzen (+ Grounding-Sektion „## Secrets" im `PromptBuilder`;
  die Verdichtung gruppiert generisch nach Category, braucht keine Änderung).
- **`GitleaksAnalyzer`** — `gitleaks dir --report-format json --no-banner <root>`; Map →
  `Category.Secrets`, Severity-Default `High`, Regel-ID/Datei/Zeile aus dem Report. Rein statisch.
- Dockerfile: `gitleaks`-Binary gepinnt + sha256. Tests via `StubProcessRunner` + Fixture.

## PR 3 — OSV-Scanner / SCA (Umriss)

- **`OsvScannerAnalyzer`** — `osv-scanner --format json -r <root>` → `Category.Sca`
  (CVE/GHSA → `RuleId`, Package/Version → `Message`). Ergänzt trivy/dotnet-sca sprach-agnostisch.
- Dockerfile: `osv-scanner`-Binary gepinnt + sha256. Tests via Stub + Fixture.

## PR 4 — Infra-Linter-Bündel (Umriss)

Drei kleine Analyzer (`Category.Sast`), die v.a. Naudits **eigene** Infra-Dateien härten:

- **`HadolintAnalyzer`** — `hadolint --format json <Dockerfile>`.
- **`ActionlintAnalyzer`** — `actionlint -format '{{json .}}'` über `.github/workflows/`.
- **`ZizmorAnalyzer`** — `zizmor --format json .github/workflows/` (GitHub-Actions-Security).

Je Dockerfile-Install (gepinnt + sha256) und je Stub-Test. Ein PR, drei Klassen + drei
Config-`case`s.

## PR 5 — LLM-Verifikations-Reducer (Umriss)

- **`Infrastructure/Sast/LlmFindingReducer : IFindingReducer`**, gewählt über `Reducer = "llm"`.
  Bekommt `IChatClient` injiziert. Prüft je Fund gegen den realen Diff-/Datei-Kontext: *bestätigen /
  verwerfen (False Positive, low value) / Message präzisieren*. **Output ⊆ Input** — keine neuen
  Funde. Default bleibt `deterministic`. **Zuletzt**, damit er beim Bau das volle Rauschen aller
  Sensoren (PR 1–4) zum Filtern hat.
- Tests: `FakeChatClient` liefert ein Keep/Drop-Urteil je Fund ⇒ verworfene fallen raus, bestätigte
  bleiben, Kontrakt `ScanFinding[]` gewahrt; Modellfehler ⇒ Fallback auf deterministische Verdichtung
  (fail-open, blockt keinen MR).
- Eigener detaillierter Spec vor Umsetzung (größter Task der Roadmap).

## Datenfluss (unverändert ggü. dem bestehenden Grounding)

```
ReviewService.ReviewAsync
  → IGitPlatform.GetChangesAsync .............. Diff (wie heute)
  → IWorkspaceProvider.CheckoutAsync ......... Klon MR/PR-Head ins Temp-Dir
  → ISastAnalyzer[*].AnalyzeAsync (parallel) . opengrep/trivy/gitleaks/osv/… → ScanFinding[]
  → InDiff annotieren · IFindingReducer.ReduceAsync (deterministic | llm)
  → PromptBuilder.Build(system, request, changes, findings) ... Diff + Grounding (jetzt inkl. Secrets)
  → IChatClient.GetResponseAsync(JsonMode) ... { summary, verdict } (wie heute)
  → Verdict-Mapping (fail-closed) · PostSummaryAsync · ws.DisposeAsync
```

Nur die Menge der Analyzer, eine neue Category und die Reducer-Wahl ändern sich — Orchestrierung,
Verdict-Logik und Core-Regel bleiben.

## Bewusste Grenzen / Caveats

- **Image wächst** um mehrere Binaries (opengrep, gitleaks, osv-scanner, hadolint, actionlint,
  zizmor) + das gepinnte Regelset. Mehr Trivy-Scan-Oberfläche im Release → die bestehende
  VEX-Ausnahme-Mechanik trägt nicht-ausnutzbare Binary-CVEs.
- **Latenz** steigt mit jedem aktiven Sensor; alle laufen parallel, Timeout pro Analyzer kappt den
  Worst Case. Sensoren sind config-abschaltbar.
- **OpenGrep-Reife:** jüngerer Fork; Engine + Regelset über gepinnte Versionen reproduzierbar, kein
  „auto"-Drift. Risiko durch Pinning + Tests beherrscht.
- **`opengrep-rules`-Scope:** bewusst kuratiert (nicht alle Sprachen) gegen Rauschen/Latenz.
- **LLM-Reducer ist opt-in:** Default bleibt deterministisch, damit BA-Messungen reproduzierbar
  sind.

## Verweise

- Vorheriger Spec (Grounding-Basis): `docs/superpowers/specs/2026-06-24-sast-review-context-design.md`
- Vorheriger Plan: `docs/superpowers/plans/2026-06-24-sast-grounding.md`
- Architektur & Extension-Points: `CLAUDE.md`
- Betroffener Code: `src/Naudit.Infrastructure/Sast/`, `src/Naudit.Infrastructure/DependencyInjection.cs`,
  `src/Naudit.Core/Models/ScanFinding.cs`, `src/Naudit.Core/Review/PromtBuilder.cs`, `Dockerfile`
- OpenGrep: <https://github.com/opengrep/opengrep> · Regeln (LGPL-2.1):
  <https://github.com/opengrep/opengrep-rules>
- Lizenz-Hintergrund: Semgrep Rules License v1.0 (Dez 2024) betrifft Registry-Regeln/`--config auto`,
  nicht die LGPL-Engine; OpenGrep-Fork hält Engine **und** Regeln unter LGPL-2.1.
- BenediktsMind: `1. Projects/Bachelorarbeit/2026-06-18 CodeRabbit – SAST-Tools + KI-Pipeline.md`
