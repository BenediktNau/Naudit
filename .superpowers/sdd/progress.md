# SDD Progress — DAST PR 2: Probing-Analyzer (Playwright-MCP über exec-stdio)

Plan: docs/superpowers/plans/2026-07-25-dast-probing-analyzer.md
Spec: docs/superpowers/specs/2026-07-19-dast-design.md (PR 2 = Probing-Analyzer)
Branch: feat/dast-probing (ab main 94ecb39, enthält gemergten App-Runner PR 1)
Start-HEAD (Task 1 BASE): 94ecb39
Baseline: 700/700 grün (`DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`, 2026-07-25)

Architektur-Entscheidung (verifiziert): Path 2 = raw duplex `docker exec` über den Socket → SDK-`StreamClientTransport(serverInput, serverOutput, loggerFactory)`; KEIN docker-CLI im Image, kein neues NuGet. E2E-Gate des App-Runners (PR 1) auf echter Engine 29.5.3 bestanden (2026-07-25); Caveat: root-startende Apps scheitern am CapDrop ALL (→ Doku Task 8).
Modelle: Implementer + Task-Reviewer = Sonnet; Final-Whole-Branch-Review = Fable.
Verifikation immer: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`.

## Tasks
- [x] Task 1: FindingCategory.Dast + PromptBuilder-Sektion
- [x] Task 2: DastOptions.MaxProbeSteps + ProbeMcpArgv
- [x] Task 3: bidirektionaler docker exec (Stdin roh / Stdout demuxt) — HÖCHSTES RISIKO, Gate = NAUDIT_TEST_DOCKER
- [x] Task 4: DastProbeSession (StreamClientTransport + McpClient, kurzlebig)
- [x] Task 5: DastAnalyzer Happy Path (Runner → Probe → Loop → JSON→ScanFinding)
- [x] Task 6: DastAnalyzer Fehlerpfade + garantierter Teardown
- [x] Task 7: DI-Verdrahtung (ISastAnalyzer) + MaxProbeSteps-Katalog
- [x] Task 8: Doku (dast.md Probing-Sektion + root-drop-Caveat, CLAUDE.md)

## Minor findings (for final review triage)
- T1 Minor: Block-Kommentar in PromtBuilder.cs:181 („Secrets zuerst … dann SAST") erwähnt DAST nicht — kosmetisch.
- T3 Minor: `ExecCreateResponse.Id` auf `string?` geweitet — neue Methode null-checkt, altes ExecAsync (:189) weiterhin `.Id` ohne Check (verhaltensgleich, war nie enforced) — Follow-up-Konsistenz.
- T3 Minor: `DemuxReadStream.Read` (sync) via `.GetAwaiter().GetResult()` — latenter Deadlock nur bei captured-context-Caller; MCP-Pfad ist async, ungenutzt.
- T3 Minor: `ConsumeHttpHeadersAsync` liest byteweise (einmaliger kleiner Header, bewusst akzeptiert).
- T5 Minor: Fail-open-Catch loggt „DAST-Probing abgebrochen" als LogWarning, deckt aber auch Routine (Probe-Container down) — evtl. Info-Level/Wording.
- T5 Minor: ParseFindings fängt nur JsonException; NRE aus valid-JSON-but-malformed liefe in den äußeren Catch (netto gleich fail-open, nur nicht lokal dokumentiert).
- T8 Minor: Fail-open-Abschnitt in dast.md ordnet Zeile 9 (Caller-Cancel) der Probing-Phase zu — DockerAppRunner hat denselben Rethrow (PR 1), Phasen-Split editorial leicht überzogen.
- T8 out-of-scope (Ticket): docs/mcp-tools.md:11 nennt DAST/Playwright noch „future slice" — jetzt stale, künftiger Doku-Fix.

## Log
(started 2026-07-25)
Task 8: complete (commit 1a10665..120d24c, review Approved, Spec ✅; docs-only 713/713). dast.md Probing-Sektion + Enablement-Korrektur + Config-Tabelle (MaxProbeSteps DB / ProbeMcpArgv + HandshakeTimeout env-only, alle gegen SettingsCatalog verifiziert) + root-drop-Caveat + Manual-Gate; CLAUDE.md-Bullet erweitert. Alle Fakten vom Reviewer gegen Quellcode geprüft, „nothing calls the runner"-Framing entfernt.
ALLE 8 TASKS KOMPLETT. Range 94ecb39(main)..120d24c auf feat/dast-probing.

## FINAL WHOLE-BRANCH REVIEW (94ecb39..120d24c) — Verdict: With fixes → nach Fix-Welle „Ready to merge: Yes"
Keine Critical, kein Leak, kein Korrektheitsfehler: Cancellation-Split (Caller-ct vs. HandshakeTimeout-Linked-CTS), Teardown-Ketten und die Demux-Zustandsmaschine von Hand getraced und für richtig befunden. Core-Regel intakt, DAST-Findings laufen durch dieselbe ScanFinding-Redaction wie SAST.
2 Important (beide gefixt in 0691171):
(1) Frame-Demux hatte KEINE schnellen Tests (nur Opt-in-Live-Docker-`cat`, 1 Frame) ⇒ `DemuxReadStream` als `DockerStdoutStream` in eigene Datei extrahiert (pure move+rename, verifiziert) + 9 deterministische Tests: Split-Reads (1 Byte/Read), EOF clean/mid-header/mid-payload, Zero-Length-Frame, Multi-Frame-Konkatenation, stderr-Discard, **State-Corruption-Fall** (teilkonsumierter stdout → stderr dazwischen → nächster stdout ⇒ „LLOWORLD", nichts verloren/dupliziert), EOF⇒0. ALLE grün im ersten Lauf — kein echter Bug, die Handverifikation ist jetzt per Ausführung bewiesen.
(2) **Prompt-Injection-Fläche** (der wertvolle Fund): DAST-Funde sind LLM-Output über vom PR-Autor kontrollierte Seiteninhalte, liefen aber unter „treat as reliable" (für Semgrep/Trivy kalibriert) ⇒ Qualifier-Satz nur an der DAST-Sektion (`AppendCategory`-Parameter, andere 3 Kategorien byte-identisch) + Positiv/Negativ-Test + Restrisiko-Abschnitt in docs/dast.md (gated nie den Merge, gleiche Redaction, kein Egress, Allowlist).
Minors-Triage: 1/2/3/4/6/7 accept, 5 (Log-Level) + 8 (mcp-tools.md „future slice" stale) Follow-up. Neu als Follow-up: Probe-Loop hat keine Wall-Clock-Grenze (nur MaxProbeSteps), asymmetrisch zum TimeBudget des Runners.

## FEATURE KOMPLETT. Range 94ecb39..0691171 (10 Commits), Suite 723/723. Re-Review der Fix-Welle: „Ready to merge: Yes".
Task 7: complete (commit f5dc560..1a10665, review Approved, Spec ✅; 713/713). DastAnalyzer als ISastAnalyzer im dastOptions.Enabled-Block (globaler IChatClient verifiziert, nicht Router), MaxProbeSteps im Katalog. 2 Wiring-Tests (enabled ⇒ „dast" da / disabled ⇒ weg). BaseSettings genügte (Ollama-Default baut IChatClient ohne Netz).
Task 6: complete (commit d5a9449..f5dc560, review Approved, Spec ✅; 711/711, probe-throws 247ms). 4 Fehlerpfad-Tests (App-Fail, non-JSON, Probe-Fehler mit 200ms-HandshakeTimeout, Caller-Cancel) — KEINE Analyzer-Änderung, nur FakeAppRunner.RunAsync +ct.ThrowIfCancellationRequested (brief-vorautorisiert, sonst wäre Cancel-Test vacuous). Reviewer traced alle 4 Pfade strukturell.
Task 5: complete (commit f2483a3..d5a9449, review Approved, Spec ✅; 707/707). DastProbePrompt + DastAnalyzer (Allowlist→RunAsync→Probe/Override→bounded Loop auf GLOBALEM IChatClient→JSON→ScanFinding(Dast)). Zweistufiger Teardown (await using app + inner finally session) strukturell verifiziert; Cancellation-Split korrekt (interner Handshake-Timeout ≠ Caller-Cancel). FakeAppRunner neu. 2 Minors (Log-Level, JsonException-only) → Triage.
Task 4: complete (commits c41f0c8..f2483a3 = Session 56f89ab + Fix f2483a3; Review Needs-fixes→Fix→Re-Review Approved, Spec ✅; 705/705). DastProbeSession (StreamClientTransport serverInput=Stdin/serverOutput=Stdout, McpClient.CreateAsync, ListTools, Client-dann-Exec-Dispose). Empirisch: CreateAsync HÄNGT bei EOF ⇒ Handshake-Timeout nötig. IMPORTANT-Fix: Timeout als DastOptions.HandshakeTimeout (Default 10s) konfigurierbar, Test 200ms (10s→258ms). Extra-Härtung: Client-Dispose auch im Catch (Brief hatte nur exec). Controller squashte versehentlichen Report-Commit weg (Feature-History code-only).
Task 3: complete (commit 80e44a9..c41f0c8, review Approved, Spec ✅; 704/704 + REAL-Docker 4/4 byte-exakt im ERSTEN Anlauf). ExecStreamAsync (attached duplex), ConnectRawAsync aus dem inline ConnectCallback extrahiert (EIN Connect, beide Nutzer), DockerExecStream (Stdin roh / Stdout demuxt via DemuxReadStream + ReadFrameAsync), status-line-agnostischer \r\n\r\n-Scan. Reviewer hat Ring-Buffer-Mathe + alle Failure-Path-Socket-Disposes von Hand getraced — keine FD-Leaks, keine Demux-Bugs. Fake + 2 ThrowingDockerClient-Stubs.
Task 2: complete (commit 5b8ce67..80e44a9, review Approved, Spec ✅; 702/702). DastOptions.MaxProbeSteps=12 + ProbeMcpArgv (node /app/cli.js …), byte-genau nach Brief, Test asserted beide Defaults.
Task 1: complete (commit 94ecb39..5b8ce67, review Approved, Spec ✅; 701/701). FindingCategory.Dast angehängt (Ordinals stabil), „DAST (dynamic)"-Sektion nach SAST; Test adaptiert an echte PromptBuilderTests-Fixtures (Brief-Helper existierten nicht), Reviewer bestätigte echte Rendering-Assertion. Enum-Exhaustiveness repo-weit gegrept: keine Switch-Gefahr.
