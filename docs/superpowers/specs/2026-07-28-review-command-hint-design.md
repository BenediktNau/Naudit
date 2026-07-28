# Kommando-Hinweis im Review (`@naudit fp` / `@naudit ok`)

**Datum:** 2026-07-28
**Status:** Design abgenommen

## Problem

`FpReplyCommand` erkennt `@naudit fp|false-positive` und `@naudit ok|angenommen|accepted` als Antwort
auf einen Inline-Kommentar; `ReviewCommentCommandService` schreibt daraus den Gedächtnis-Eintrag bzw.
die Finding-Resolution. Nur: **in keinem geposteten Kommentar steht, dass es diese Kommandos gibt.**
Wer den Bot nicht kennt — insbesondere ein AI-Agent, der den PR abarbeitet — kann sie nicht nutzen.

Ziel: Der Hinweis steht dort, wo geantwortet werden muss (am Inline-Kommentar), ohne die Kommentare
für Menschen zuzumüllen.

## Lösung im Überblick

Zwei Hinweis-Varianten, aus derselben Quelle gebaut:

| Ort | Form | Sichtbar für Menschen |
|---|---|---|
| Jeder Inline-Kommentar | HTML-Kommentar `<!-- naudit:commands … -->` | nein |
| Summary-Kommentar | `<details>`-Klapper, eine Zeile | ja, zugeklappt |

GitHub wie GitLab entfernen HTML-Kommentare beim Rendern, liefern sie aber im rohen Body der
API aus (`gh api`, `gh pr view --comments`, GitLab Notes-API) — genau das, was ein Agent liest.

**Bewusst akzeptierte Grenzen:** Ein Agent, der die *gerenderte* Weboberfläche liest (Browser-
Automation) statt der API, sieht den Inline-Hinweis nicht. Für Menschen ist die Funktion genau
einmal pro Review über den `<details>`-Klapper in der Summary auffindbar — dafür ist er da.

## Komponenten

### 1. `ReviewCommandHint` (neu, Core)

`src/Naudit.Core/Review/ReviewCommandHint.cs` — statische Klasse, hängt nur an
`ReviewResolutionOptions`, kennt keine Plattform (Core-Regel unberührt).

```csharp
public static string Inline(ReviewResolutionOptions options);   // HTML-Kommentar oder ""
public static string Summary(ReviewResolutionOptions options);  // <details>-Block oder ""
```

Regeln:

- `RenderHint == false` ⇒ beide liefern `""` (heutiges Verhalten, kein Zeichen Unterschied).
- `Resolution.Enabled == false` ⇒ `@naudit ok` wird **nicht** genannt. Begründung: `ok` wird in
  dem Fall still verworfen (`ReviewCommentCommandService.cs:95`), `fp` funktioniert weiter (der
  Gedächtnis-Eintrag hängt nicht an dem Schalter). Ein Hinweis auf ein totes Kommando ist
  schlimmer als kein Hinweis.
- Texte deutsch wie der Rest der Summary. Der Autorisierungs-Hinweis bleibt plattform-neutral
  formuliert ("Repo-Mitglieder, Developer/Collaborator aufwärts") — Core weiß nicht, ob GitHub
  (`author_association` ∈ OWNER/MEMBER/COLLABORATOR) oder GitLab (Access-Level ≥ 30) prüft.

Inline-Block:

```
<!-- naudit:commands
Antworte AUF DIESEN KOMMENTAR (Reply im selben Thread, kein neuer Top-Level-Kommentar).
Erste Zeile der Antwort, genau eines:
  @naudit fp <grund>   - Fehlalarm; Naudit merkt sich das dauerhaft fuer dieses Projekt.
  @naudit ok <text>    - Finding angenommen/umgesetzt.
Nur Repo-Mitglieder (Developer/Collaborator aufwaerts) sind autorisiert.
-->
```

Summary-Block:

```markdown
<details><summary>🤖 Naudit-Kommandos</summary>

Antworte im Thread eines Inline-Kommentars — erste Zeile der Antwort:

- `@naudit fp <grund>` — Fehlalarm. Naudit merkt sich das für dieses Projekt und meidet den Fund künftig.
- `@naudit ok <text>` — Finding angenommen/umgesetzt.

Nur Repo-Mitglieder (Developer/Collaborator aufwärts) sind autorisiert. Ein neuer Top-Level-Kommentar
wird nicht ausgewertet, es muss eine Antwort auf den Kommentar sein.
</details>
```

### 2. Anbindung in `ReviewService`

Der Hinweis hängt **nur an der geposteten Kopie**, nicht an der gespeicherten:

```csharp
var postInline = inline
    .Select(c => c with { Body = c.Body + ReviewCommandHint.Inline(options.Resolution) })
    .ToList();
var postSummary = summary + ReviewCommandHint.Summary(options.Resolution);
var posted = await gitPlatform.PostReviewAsync(request, postSummary, postInline, verdict, ct);
await RecordAuditAsync(request, verdict, summary, inline, orphans, posted, …);
return new ReviewResult(summary, verdict);
```

Begründung: `ReviewFindingEntity.Body` und `ReviewEntity.Summary` bleiben frei vom Boilerplate.
Sonst schleppten WebUI-Review-Detail, Analytics-Aggregation und der geplante LLM-Klassifikator
(Analytics PR 4) den Block in jedem einzelnen Finding mit.

`postInline` ist index-gleich zu `inline`, damit das Zippen der zurückgegebenen `PostedComment`s
auf die Audit-Findings (`PlatformCommentId`-Erfassung, Review-Memory PR 2a) unverändert trägt.

### 3. Konfiguration

`ReviewResolutionOptions.RenderHint` (`bool`, Default `true`) neben `RenderCheckbox`, plus
`new("Naudit:Review:Resolution:RenderHint", false)` im `SettingsCatalog` ⇒ DB-verwaltet und in der
WebUI über die Raw-keys-Ansicht editierbar — genau wie die übrigen `Resolution:*`-Schalter, für die
es (noch) kein eigenes Panel unter Review rules gibt. Der Hinweis geht in *jeden* Kommentar jedes
Reviews — das will man abschalten können, ohne das Resolution-Tracking zu opfern.

## Fehlerverhalten

Reine String-Komposition ohne I/O — kein neuer Fehlerpfad, kein Fail-open-Bedarf.

## Tests (TDD, red → green)

**`ReviewCommandHintTests`**
- `RenderHint=false` ⇒ `Inline` und `Summary` leer.
- `Resolution:Enabled=false` ⇒ Block nennt `fp`, aber nicht `ok`.
- `Inline` ist ein wohlgeformter HTML-Kommentar: beginnt mit `<!--`, endet mit `-->`, enthält
  dazwischen kein `-->` (das würde den Block auf der Plattform aufbrechen und den Rest sichtbar
  machen).

**`ReviewServiceTests`**
- Der an `IGitPlatform.PostReviewAsync` übergebene Inline-Body trägt den Hinweis, der an
  `IReviewAuditSink` übergebene Body **nicht**; dasselbe für die Summary.
- `RenderHint=false` ⇒ geposteter Body identisch zu heute.

**Kopplungs-Guard**
- Die im Hinweistext genannten Kommandozeilen werden durch `FpReplyCommand.TryParse` geschickt
  und müssen als `FalsePositive` bzw. `Accept` parsen. Verhindert, dass Hinweis und Parser
  auseinanderlaufen, wenn einer von beiden später angefasst wird.

## Doku

`docs/review-memory.md` (Kommando-Abschnitt), `docs/review-analytics.md`,
`docs/configuration.md` (neuer Schlüssel), `CLAUDE.md`.
