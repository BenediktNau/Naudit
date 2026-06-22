# Design: Inline-/Positions-Kommentare (CodeRabbit-Stil)

*2026-06-22 · Projekt: Naudit*

## Ziel

Statt nur **einen** Summary-Kommentar zu posten, soll Naudit Befunde **an den konkreten
Codestellen** im Diff kommentieren — wie CodeRabbit. Gewählt ist der **schlanke Hybrid**:

- Jede Finding, die sich sicher einer Diff-Zeile zuordnen lässt, wird als **Inline-Kommentar**
  an genau diese Datei+Zeile gepostet.
- Zusätzlich **ein** kurzer Summary-Kommentar mit Verdict, Finding-Anzahl und denjenigen Findings,
  die sich **keiner** Zeile sicher zuordnen lassen (global/„ganze Datei").

Damit bleibt die Redundanz minimal (keine Doppelung jeder Finding in Summary **und** inline) und es
gibt einen robusten Fallback für nicht-verortbare Befunde.

**Beide Plattformen** (GitLab + GitHub) werden in einem Zug umgesetzt. Der Review-Kern
(`ReviewService`) bleibt auslöser-unabhängig; Webhook- **und** `POST /review`-Pfad profitieren
automatisch. `Naudit.Core` bleibt SDK-frei (Diff-Parsing und Validierung sind reine .NET-Logik,
das LLM läuft weiter über die MEAI-Abstraktion).

## Entscheidungen

- **Ausgabeform: schlanker Hybrid** (Inline + ein Summary mit den nicht-verorteten Findings).
  Verworfen: voller Hybrid (jede Finding doppelt) und „nur Inline" (kein Summary, Verdict ginge
  verloren bzw. nur via Rückgabe).
- **Zeilen-Scope: hinzugefügte *und* Kontextzeilen** sind kommentierbar (`+`- und ` `-Zeilen im
  Hunk). Gelöschte (`-`)-Zeilen sind **nicht** kommentierbar (Findings darauf wandern in den
  Summary). Das bestimmt das GitLab-`position`-Mapping (Kontextzeilen brauchen `old_line` **und**
  `new_line`).
- **Positions-Strategie: Diff selbst parsen + LLM-Findings validieren** (Ansatz A). `Naudit.Core`
  parst die ohnehin vorhandenen `CodeChange.Diff`-Unified-Diffs zu einer Menge kommentierbarer
  Zeilen und prüft **jede** LLM-Finding dagegen. Verworfen: LLM-Zeilen ungeprüft durchreichen
  (fragil, ungültige API-Positionen) und Diff-Offset vom LLM zählen lassen (LLMs zählen Offsets
  unzuverlässig).
- **Prompt annotiert echte Zeilennummern.** `PromptBuilder` schreibt vor jede Diff-Zeile ihre
  New-File-Zeilennummer, damit das LLM stabile, reale Zeilennummern referenzieren kann.
- **Eine generische `InlineComment`-Abstraktion in Core**; jede Plattform mappt sie auf ihre API.
  GitLab und GitHub teilen sich dieselbe validierte Wahrheit, kein doppeltes Mapping in Core.
- **GitHub postet atomar über die Reviews-API** (ein Call: `body` = Summary + `comments[]`).
  **Verdict bleibt *nicht* der GitHub-Review-State** (`event: "COMMENT"`, nicht `REQUEST_CHANGES`/
  `APPROVE`) — Naudit blockt nicht über GitHubs eigenen Review-Status; das Verdict wird wie bisher
  über `ReviewResult` fürs CI-Gate transportiert. (Vermeidet zusätzliche Approve-Permissions und
  ein unerwartetes Hard-Block durch den Bot.)
- **`Naudit.Core` bleibt SDK-frei.** Die Abhängigkeitsregel (`Web → Infrastructure → Core`, Core
  nur an MEAI-Abstractions) bleibt intakt.

## Komponenten

### 1. Domänenmodell + Diff-Parser (`Naudit.Core`)

- Neues Modell `InlineComment(string FilePath, int NewLine, int? OldLine, string Body)`:
  - `NewLine` — Zeilennummer in der neuen Datei (nutzen GitHub **und** GitLab).
  - `OldLine` — nur bei **Kontextzeilen** gesetzt (GitLab braucht für `position` beide); bei
    hinzugefügten (`+`)-Zeilen `null`.
- Neuer `DiffParser` (statische, reine Klasse, kein SDK): parst Unified-Diff-Hunks
  (`@@ -a,b +c,d @@`) und liefert pro Datei eine Map **kommentierbarer Zeilen**
  `NewLine → OldLine?`:
  - ` ` (Kontext): `old++`, `new++` → Eintrag `(new → old)`.
  - `+` (hinzugefügt): `new++` → Eintrag `(new → null)`.
  - `-` (gelöscht): `old++` → **kein** Eintrag (nicht kommentierbar).
  - Mehrere Hunks pro Datei werden zusammengeführt. `\ No newline at end of file` u. Ä. werden
    ignoriert.
- **Keine** neuen Provider-/Plattform-Abhängigkeiten.

### 2. LLM-Antwortschema + `PromptBuilder` (`Naudit.Core`)

- Antwortschema wird erweitert auf:
  ```json
  {
    "verdict": "approve" | "request_changes",
    "summary": "kurzer Markdown-Überblick",
    "comments": [
      { "file": "src/Foo.cs", "line": 42, "comment": "…" }
    ]
  }
  ```
  `summary` = knapper Gesamtüberblick; die eigentlichen Befunde stehen in `comments` (jeweils mit
  `file` + `line` = annotierte New-File-Zeilennummer + `comment`-Markdown).
- `PromptBuilder.Build` annotiert jede Diff-Zeile mit ihrer New-File-Zeilennummer (z. B.
  `  42 + var x = foo();` / `  43   context`). Der System-Prompt erklärt das Schema und die
  Konvention „kommentiere mit der links gezeigten Zeilennummer; betrifft eine Finding keine
  konkrete Zeile, lass `comments` aus und schreib sie in `summary`".

### 3. `ReviewService`-Ablauf (`Naudit.Core`)

1. `GetChangesAsync` → bei **keinen** Changes: `ReviewVerdict.Approve`, nichts posten (wie heute).
2. `PromptBuilder.Build` (annotiert).
3. `IChatClient.GetResponseAsync` (JSON-Mode wie heute) → `{verdict, summary, comments[]}`.
4. `DiffParser` baut pro Datei die Menge kommentierbarer Zeilen.
5. **Validierung** je `comment`:
   - Datei in den Changes **und** `line` in der kommentierbaren Menge → `InlineComment`
     (mit passendem `OldLine` aus der Map).
   - sonst → **nicht-verortete Finding** (Text gemerkt).
6. **Summary zusammensetzen:** LLM-`summary` + Verdict-Zeile + Finding-Count (inline / ohne
   Position) + Abschnitt „Findings ohne Position" mit den nicht-verorteten Findings.
7. `IGitPlatform.PostReviewAsync(request, summaryMarkdown, inlineComments)`.
8. Rückgabe `ReviewResult(summaryMarkdown, verdict)` (CI-Gate unverändert).

- Verdict-Mapping bleibt **fail-closed** (nur explizites `approve`/`request_changes`; sonst
  Exception) wie heute.

### 4. Plattform-Interface (`Naudit.Core.Abstractions`)

`PostSummaryAsync` wird zu:
```csharp
Task PostReviewAsync(ReviewRequest request, string summaryMarkdown,
                     IReadOnlyList<InlineComment> comments, CancellationToken ct = default);
```
- Leere `comments` ⇒ verhält sich wie der bisherige Summary-Only-Post.
- Vertrag: postet den Summary **und** alle Inline-Kommentare; wirft bei API-Fehlern (wie heute via
  `EnsureSuccessStatusCode`).

### 5. GitLab-Mapping (`Naudit.Infrastructure/Git/GitLab`)

- Summary → unverändert `POST …/notes` mit `{ body }`.
- Pro `InlineComment` → `POST …/merge_requests/{iid}/discussions` mit
  ```json
  { "body": "<Body>",
    "position": { "position_type": "text",
                  "base_sha": "…", "head_sha": "…", "start_sha": "…",
                  "new_path": "<FilePath>", "new_line": <NewLine>,
                  "old_path": "<FilePath>", "old_line": <OldLine?>  // nur bei Kontextzeile
  } }
  ```
- Die `diff_refs` (`base_sha`/`head_sha`/`start_sha`) holt `GitLabPlatform` **zur Post-Zeit**
  stateless per `GET …/merge_requests/{iid}` (Response enthält `diff_refs`). Neuer DTO dafür.

### 6. GitHub-Mapping (`Naudit.Infrastructure/Git/GitHub`)

- **Ein** Call `POST repos/{owner/repo}/pulls/{n}/reviews`:
  ```json
  { "body": "<Summary>", "event": "COMMENT",
    "comments": [ { "path": "<FilePath>", "line": <NewLine>, "side": "RIGHT", "body": "<Body>" } ] }
  ```
  `side: "RIGHT"` deckt hinzugefügte **und** Kontextzeilen über die New-File-Zeilennummer ab
  (kein `OldLine` nötig).
- Leere `comments` ⇒ Review nur mit `body` (entspricht dem bisherigen Issue-Kommentar inhaltlich).

## Datenfluss

```
ReviewService.ReviewAsync:
  1) IGitPlatform.GetChangesAsync ──REST──▶ GitLab/GitHub
     (keine Changes ⇒ Verdict=Approve, nichts posten, fertig)
  2) PromptBuilder.Build  → Diff mit New-File-Zeilennummern annotiert
  3) IChatClient.GetResponseAsync (JSON) ─▶ LLM
        ◀─ { verdict, summary, comments:[{file,line,comment}] }
  4) DiffParser.Parse(changes) → pro Datei { NewLine → OldLine? }
  5) je comment validieren:
        Treffer  → InlineComment(FilePath, NewLine, OldLine?, Body)
        kein Tr. → nicht-verortete Finding (in Summary)
  6) Summary = LLM-summary + Verdict + Count + „Findings ohne Position"
  7) IGitPlatform.PostReviewAsync(request, summary, inlineComments)
        GitLab: /notes (Summary) + N × /discussions (position via diff_refs)
        GitHub: 1 × /pulls/{n}/reviews (body + comments[], event=COMMENT)
  8) return ReviewResult(summary, verdict)
```
Webhook- und `POST /review`-Pfad bleiben unverändert (beide rufen `ReviewService.ReviewAsync`).

## Tests (TDD, spiegeln das bestehende Vorgehen)

- **Core – `DiffParserTests`** (rein): hinzugefügte Zeile → `(new, null)`; Kontextzeile →
  `(new, old)`; gelöschte Zeile → kein Eintrag; mehrere Hunks; mehrere Dateien; Zeilennummern
  korrekt aus `@@`-Headern.
- **Core – `PromtBuilderTests`** (bestehende Datei): Diff-Zeilen werden mit korrekten
  New-File-Zeilennummern annotiert.
- **Core – `ReviewServiceTests`** (mit `FakeChatClient`/`FakeGitPlatform`, Letzteres fängt jetzt
  `summary` + `InlineComment[]`):
  - Findings mit gültiger Zeile → landen als Inline-Comments;
  - Finding mit ungültiger/nicht kommentierbarer Zeile → landet im Summary, nicht inline;
  - keine Changes → nichts gepostet, Verdict=Approve;
  - Verdict-Mapping bleibt fail-closed.
- **Infrastructure – `GitLabPlatformTests`** (`StubHttpMessageHandler`): `/discussions`-Payload
  mit korrektem `position` (inkl. `old_line` nur bei Kontext) + `diff_refs`-GET.
- **Infrastructure – `GitHubPlatformTests`** (`StubHttpMessageHandler`): `/reviews`-Payload mit
  `body` + `comments[]` (`path`/`line`/`side`).

## Bewusste Grenzen / Non-Goals

- **Keine De-Dup/Idempotenz** wiederholter Webhook-Events (eigener Board-Eintrag) — ein erneutes
  Event postet erneut. Bleibt POC-Grenze.
- **Robustheit bei Restfehlern:** Positionen sind vorab validiert; sollte eine API eine Position
  dennoch ablehnen (z. B. GitLab-Edge-Cases), schlägt der Post fehl (`EnsureSuccessStatusCode`) —
  kein per-Kommentar-Fallback im POC.
- **Gelöschte Zeilen** sind nicht kommentierbar (bewusster Scope).
- **GitHub-Review-State** wird nicht als Gate genutzt (`event=COMMENT`); Verdict bleibt über
  `ReviewResult`.
- **Keine Diff-Größen-Begrenzung / kein `.naudit.yml`** — separate Board-Einträge.

## Verweise

- Repo-Architektur: `CLAUDE.md`
- Vorheriger Spec: `docs/superpowers/specs/2026-06-18-ci-review-endpoint-design.md`
- Bestehender Plan: `docs/superpowers/plans/2026-06-16-naudit-codereview-bot.md`
- Board-Eintrag: `1. Projects/Naudit/Doings.md` → „Erweiterung: Inline-/Positions-Kommentare"
