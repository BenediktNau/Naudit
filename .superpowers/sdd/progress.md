# SDD Progress — MCP-Tools in der Review-Runtime (Context7), Slice A

Plan: docs/superpowers/plans/2026-07-11-mcp-context7-review.md
Spec: docs/superpowers/specs/2026-07-11-mcp-context7-review-design.md
Branch: feat/mcp-context7 (ab main f2fe20f)
Worktree: /home/bnau/workspace/Naudit/.claude/worktrees/feat-mcp-context7
Task 1 BASE: 77caa1d (angepasster Plan-Commit / Branch-HEAD beim Start)

Basis-Entscheidung: auf main (unabhängiger PR), NICHT auf feat/author-sessions gestapelt.
Der IAiClientRouter-Seam fehlt hier bewusst — ReviewService nutzt IChatClient direkt.

Verifikation: `dotnet test Naudit.slnx` (aus dem Worktree-Root).
Modelle: Implementer + Task-Reviewer = Sonnet; Final-Whole-Branch-Review = Opus.

## Tasks
- [x] Task 1: IReviewToolProvider-Naht + ReviewService-Wiring (Verhalten unverändert)
- [x] Task 2: PromptBuilder Tool-Guidance-Block
- [x] Task 3: McpOptions + Config-Binding + SettingsCatalog-Scalars
- [x] Task 4: McpReviewToolProvider (fail-open + cache, Fake-Connector-getestet)
- [x] Task 5: ClaudeCode-CLI-Pfad (--mcp-config / --allowedTools / --max-turns) [+ Härtungs-Fix]
- [x] Task 6: echter McpClientToolConnector + Paket + DI (Function-Invocation)
- [x] Task 7: Docs

## Minor findings (for final review triage)
- T1 Minor: DI registriert `new NullReviewToolProvider()` als Instanz statt `AddSingleton<IReviewToolProvider,NullReviewToolProvider>()` — funktional identisch (stateless/sealed), aus dem Brief-Snippet; Task 6 ersetzt die Zeile ohnehin durch die konditionale Registrierung. „Know it's here", kein Defekt.
- T2 Minor (kosmetisch): AppendToolGuidance sitzt zwischen AppendFindings und dessen Helper AppendCategory (wie im Brief); unten anhängen läse minimal sauberer.
- T3 Minor: McpOptionsTests Catalog-Test prüft nur TryGet==true, nicht IsSecret==false (wie im Brief). Billiger Follow-up.
- T4 Minor: McpReviewToolProvider cached JEDES non-empty Aggregat, auch ein partielles (Server a down, b ok ⇒ b-Tools für Prozesslaufzeit gecacht; a wird bis Neustart nicht mehr aufgenommen). Spec-konform ("non-empty cached"), aber operative Kante — 1-Zeilen-Kommentar/Design-Follow-up wert.
- T4 Minor: `_cached` = exakt dieselbe List<AITool>-Instanz als IReadOnlyList exponiert; Downcast könnte Cache mutieren. Defensive .AsReadOnly()/Copy entfernte das Risiko.
- T4 Minor: SemaphoreSlim nie disposed (harmlos für Prozess-Singleton).
- T4 Minor (Coverage): kein Test für "keine Server, Enabled=true" und kein Test, der den Empty→Retry-Pfad (höhere CallCount) assert't.
- T5 Minor (security-adjacent, FINAL-REVIEW erwägen zu fixen): Temp-Datei-Write+chmod (ClaudeCodeChatClient.cs:52-55) liegen VOR dem try/finally — wirft WriteAllTextAsync/SetUnixFileMode, bleibt evtl. eine key-tragende Datei mit Default-Perms liegen (nur bei FS-Fehler, negligible). Fix: Write+chmod ins try ziehen.
- T5 Minor (security-adjacent): IsValidServerName nutzt `^...$`; `$` matcht auch vor einzelnem trailing `\n` (z.B. "context7\n" passiert). Kann KEIN volles Extra-Token einschleusen (harmlos für Bash-Injection), aber `\A[A-Za-z0-9_-]+\z` wäre defensiv exakt.
- T5 Minor (Coverage): kein Test für 0600-Perms bzw. Datei-Löschung nach dem Lauf (Verhalten per Inspektion korrekt).
- T6 Minor: McpClientToolConnector.ConnectAndListAsync disposed den McpClient nie (by design — Tools bleiben prozesslebenslang callable). Wirft ListToolsAsync NACH CreateAsync (transienter Protokoll-Fehler), leakt der verbundene Client/Transport still statt vor dem Propagieren disposed zu werden. Low impact (Singleton, seltener Pfad). `await using`-Reconsider als Follow-up.

## Log
(started 2026-07-11)
Task 7: complete (commit cb3ccc5..b0478eb + Doc-Fix 9bc51d2, review Approved, Spec ✅; docs-only, kein Test). docs/mcp-tools.md (zwei Provider-Pfade, opt-in/fail-open, voller Config-Block, MaxIterations-Cap, Future-Slice-B/C-Pointer, Extending-Seam) + configuration.md-Subsektion+3 Key-Zeilen. Alle Fakten vom Reviewer gegen den Code geprüft (Defaults, Katalog nur Enabled/MaxIterations, ApiKey env-only, Allowlist mcp__ only). Reviewer-Important (out-of-diff, feature-verursacht): claudecode-provider.md sagte "kein agentischer Review/keine Tools" — jetzt stale bei MCP-on ⇒ Controller-Fix 9bc51d2 (1-Zeilen-Caveat + Link in Intro+Non-goals). 1 kosmetischer Minor (Cross-Links) → Triage.
ALLE 7 TASKS KOMPLETT. Range f2fe20f..9bc51d2 auf feat/mcp-context7. Nächster Schritt: Final-Whole-Branch-Review (opus).

## FINAL WHOLE-BRANCH REVIEW (opus, f2fe20f..9bc51d2) — Verdict: Ready to merge — WITH FIXES
Keine Critical, keine blockierenden Important. Core-Regel intakt (ModelContextProtocol nur Infrastructure). Beide DI-Guards strukturell gekoppelt (gleiche Locals) ⇒ "Tools gesetzt ⟹ Client gewrappt" per Konstruktion, kaputte Kombi unerreichbar; McpDiCompositionTests deckt alle 3 Zellen. Kein Double-Path/kein Secret-auf-argv (Tests pinnen argv-Absenz des Keys + Pfad-statt-inline). Off-by-default byte-identisch (strukturell: Early-Return-Guidance, --model zuletzt). Fail-open = failed-SAST-Präzedenz (echte Cancellation propagiert). Recovery/Setup-Probe unbeeinflusst (lazy Registrierungen).
2 fix-before-merge (security-boundary, beide billig, nicht exploitierbar): (T5) Temp-Datei-Perm-Window vor chmod → atomar 0600 via FileStreamOptions.UnixCreateMode; (T5) Validator-Anchor ^..$ → \A..\z. Beide + Egress-Doku + DI-Guard-Dedup in Fix-Commit e62cfb7 (Sonnet) umgesetzt; 17/17 ClaudeCode-Tests, 361/361 full single-threaded (inkl. neuem 0600-Perms-Test). Übrige Minors acceptable-as-follow-up.
Neue Follow-ups (nicht merge-blockierend, für Ticket): #3 PromptBuilder hardcodet "Context7" (parametrisieren wenn 2. Tool kommt); T6 McpClient-Leak bei ListToolsAsync-Throw (bei stdio = Kind-Prozess-Leak → try/catch+DisposeAsync); T4 Partial-Cache-Multi-Server-Edge (Kommentar/Redesign wenn >1 Server).

## DEFERRED MANUAL E2E (Pflicht-Gate VOR MCP-Aktivierung in Prod, kein Code-Defekt)
Task 6 Step 6: echter claude-CLI + Live-Context7 + laufender Host. Primär zu validieren (Review-Concern #2): ResponseFormat=Json + Function-Invocation-Tool-Loop koexistieren auf dem Ziel-Modell (manche Provider könnten JSON auf einem Tool-Call-Turn erzwingen; nicht-JSON im Final-Turn ⇒ Review fail-closed, NICHT fail-open). Guidance-Zeile "After any tool use, still respond with the required review JSON" mitigiert.

## FEATURE KOMPLETT. Range f2fe20f..e62cfb7 auf feat/mcp-context7. Bereit für finishing-a-development-branch (Push + PR; Benedikt merged selbst).
Task 6: complete (commit b779dcb..cb3ccc5, review Approved, Spec ✅; 3/3 McpDiCompositionTests, 360/360 single-threaded). ModelContextProtocol.Core 1.4.1 NUR in Infrastructure.csproj (Core-Regel per grep verifiziert), McpClientToolConnector (HttpClientTransport AutoDetect + Authorization-Bearer / StdioClientTransport, McpClient.CreateAsync, ListToolsAsync((RequestOptions?)null)→IList<McpClientTool>, [.. tools]). DI: UseFunctionInvocation(MaximumIterationsPerRequest=MaxIterations)-Wrap + konditionale McpReviewToolProvider/NullReviewToolProvider-Registrierung, beide unter identischer Bedingung Enabled && Provider!=ClaudeCode (ersetzt T1-Default). Reviewer hat SDK-Reconciliation per Reflection gegen die echte 1.4.1-DLL bestätigt (Return-Typen, Overloads, McpClientTool:AIFunction:AITool). MANUELLES E2E (Step 6) DEFERRED — kein Live-Context7/CLI/Host. 1 Minor (Client-Leak bei ListTools-Throw) → Triage.
Task 5: complete (commits ebd6380..b779dcb = orig 563414d + Härtungs-Fix b779dcb; review Needs-fixes→Fix→Re-review Approved, Spec ✅; 16/16 fokussiert, 357/357 single-threaded). ClaudeCodeChatClient MCP-Zweig (--mcp-config PFAD/--allowedTools mcp__<server> only/--max-turns N), AiClientFactory.Create(AiOptions,McpOptions?), DI-Callsite. Zwei HARD-Invarianten: MCP-on Allowlist NUR mcp__<server> (kein Bash/Edit/Read), MCP-off byte-identisch. Erste Review: 2 Important — (1) ApiKey auf argv, (2) Server-Name-Allowlist-Injection. Fix (b779dcb, Benedikt wählte Temp-Datei): --mcp-config-JSON in 0600-Temp-Datei (Pfad statt inline, try/finally-Cleanup, Cancellation nicht geschluckt) + fail-closed Name-Validierung ^[A-Za-z0-9_-]+$ vor Allowlist + --model zuletzt (off byte-identisch). Fix-Subagent starb an API-Fehler VOR Test/Commit ⇒ Controller verifizierte (grün) + committete. Re-Review: beide Important gefixt, echte Tests (argv-Absenz des Keys, invalid-name-throws). Extra: Program.cs AiTestClientFactory method-group→Lambda (Compile-Break durch neuen Optional-Param, behavior-preserving). 3 security-adjacent Minors → Final-Review-Triage.
Task 4: complete (commit 544536f..ebd6380, review Approved, Spec ✅; 4/4 fokussiert, 353/353 single-threaded). IMcpToolConnector-Seam + McpReviewToolProvider (implements Core IReviewToolProvider): aggregiert Server, fail-open (`when (!ct.IsCancellationRequested)` ⇒ echte Cancellation propagiert, Server-Fehler geschluckt+geloggt), cached NUR non-empty (leer ⇒ retry). Double-checked-lock via SemaphoreSlim korrekt (Read/Write immer in Critical Section). 4 Tests assert echt (Identität t2, CallCount). Core untouched, kein ModelContextProtocol-Paket. 4 Minors → Triage.
Task 3: complete (commit 496053c..544536f, review Approved, Spec ✅; 349/349 single-threaded, 2 neue McpOptionsTests). McpOptions/McpServerConfig (Enabled/MaxIterations=4/Servers; Server Name/Transport=http/Url/Command/Arguments/ApiKey), DI bindet+AddSingleton(mcpOptions) vor IChatClient-Registrierung (exakte Position für T5/T6), 2 Katalog-Scalars (beide non-secret), Servers/ApiKey bewusst env-only (nicht im Katalog). Kein Core, kein ModelContextProtocol-Paket eingeschmuggelt (verifiziert). 1 Minor (Katalog-Test prüft IsSecret nicht) → Triage.
Task 2: complete (commit f74b2b1..496053c, review Approved, Spec ✅; 347/347 single-threaded, 29/29 fokussiert). PromptBuilder.Build +toolsAvailable (trailing optional, default false), AppendToolGuidance = echter Early-Return bei false ⇒ default-off-Prompt byte-identisch (strukturell, nicht nur getestet). ReviewService holt Tools jetzt VOR Build, reicht toolsAvailable durch; Chat-Call unverändert direktes chatClient.GetResponseAsync (kein Router). 2 neue Tests assert echte Präsenz/Absenz. 1 kosmetischer Minor (Helper-Platzierung, wie im Brief) → Triage.
Task 1: complete (commits 77caa1d..f74b2b1, review Approved, Spec ✅; 345/345 grün, 2 neue Tests assert echt auf ChatOptions.Tools). IReviewToolProvider+NullReviewToolProvider (Core, nur MEAI.Abstractions — Core-Regel per Standalone-Build verifiziert), ReviewService-Ctor +toolProvider (main-Form, direktes IChatClient), DI-No-Op-Default. Extra-Fix ReviewAuditSinkTests.cs (2. direkte new ReviewService(-Stelle) — legitim/minimal, per grep bestätigt einzige weitere Stelle; SastWiringTests nutzt DI). 1 Minor → Triage.
