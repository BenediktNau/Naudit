# Kommando-Hinweis im Review (`@naudit fp` / `@naudit ok`) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Jeder geposteten Review-Kommentar trägt einen für Menschen unsichtbaren Hinweis, dass man
mit `@naudit fp <grund>` / `@naudit ok <text>` darauf antworten kann — damit ein AI-Agent die
Kommandos ohne Vorwissen nutzt.

**Architecture:** Eine neue statische Core-Klasse `ReviewCommandHint` baut zwei Textbausteine aus
`ReviewResolutionOptions`: einen HTML-Kommentar (`<!-- naudit:commands … -->`, von GitHub/GitLab
beim Rendern verschluckt, im Raw-Body der API sichtbar) für jeden Inline-Kommentar und einen
zugeklappten `<details>`-Block für die Summary. `ReviewService` hängt beide **nur an die an
`IGitPlatform.PostReviewAsync` übergebene Kopie**, nie an die Texte, die ins Audit/in die DB gehen.

**Tech Stack:** C# / .NET 10, xUnit. Keine neuen Pakete, keine Migration, keine Frontend-Änderung.

**Spec:** `docs/superpowers/specs/2026-07-28-review-command-hint-design.md`

## Global Constraints

- Core-Regel: `Naudit.Core` darf nur `Microsoft.Extensions.AI.Abstractions` kennen — die neue Klasse
  liegt in Core und darf **keine** Plattform-Typen (GitHub/GitLab) berühren. Der
  Autorisierungs-Hinweis bleibt deshalb plattform-neutral formuliert.
- Code-Kommentare auf Deutsch (Repo-Konvention).
- Solution-Datei ist `Naudit.slnx`, **nicht** `Naudit.sln`.
- Der Inline-Block ist ein HTML-Kommentar: sein Inneres darf **nirgends** die Zeichenfolge `-->`
  oder `--` enthalten — sonst bricht der Kommentar auf der Plattform auf und der Rest wird sichtbar.
  Deshalb im versteckten Block nur einfache Bindestriche (`-`), keine Gedankenstriche (`—`) und
  keine Umlaute (ASCII).
- Default-Verhalten: `RenderHint = true`. `RenderHint = false` muss byte-identisch zum heutigen
  Verhalten führen (leerer String, kein Zeilenumbruch zu viel).

## File Structure

| Datei | Verantwortung |
|---|---|
| `src/Naudit.Core/Review/ReviewCommandHint.cs` (neu) | Baut Inline- und Summary-Hinweis aus `ReviewResolutionOptions`. Reine String-Komposition, kein I/O. |
| `src/Naudit.Core/Review/ReviewOptions.cs` (ändern) | `ReviewResolutionOptions.RenderHint` (bool, Default `true`). |
| `src/Naudit.Core/Review/ReviewService.cs` (ändern, ~Zeile 125-128) | Hängt die Hinweise an die gepostete Kopie; Audit/Rückgabe bleiben sauber. |
| `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs` (ändern, ~Zeile 83) | Macht den Schlüssel DB-verwaltet/WebUI-editierbar. |
| `tests/Naudit.Tests/ReviewCommandHintTests.cs` (neu) | Verhalten der Bausteine + Kopplungs-Guard gegen `FpReplyCommand`. |
| `tests/Naudit.Tests/ReviewServiceTests.cs` (ändern) | Gepostete Kopie trägt den Hinweis, Audit nicht. |
| `docs/*` (ändern) | Kommando-Hinweis dokumentieren, neuen Schlüssel eintragen. |

---

### Task 1: `ReviewCommandHint` + Konfigurationsschalter

**Files:**
- Create: `src/Naudit.Core/Review/ReviewCommandHint.cs`
- Modify: `src/Naudit.Core/Review/ReviewOptions.cs:78-83` (Klasse `ReviewResolutionOptions`)
- Modify: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs:83`
- Test: `tests/Naudit.Tests/ReviewCommandHintTests.cs`

**Interfaces:**
- Consumes: `ReviewResolutionOptions` (existiert, `src/Naudit.Core/Review/ReviewOptions.cs`) mit
  `bool Enabled`; `FpReplyCommand.TryParse(string?) → ParsedReviewCommand?` und
  `ReviewCommandKind { FalsePositive, Accept }` aus `Naudit.Infrastructure.Git` (nur im Test).
- Produces:
  - `ReviewResolutionOptions.RenderHint` (`bool`, Default `true`)
  - `static string Naudit.Core.Review.ReviewCommandHint.Inline(ReviewResolutionOptions options)`
  - `static string Naudit.Core.Review.ReviewCommandHint.Summary(ReviewResolutionOptions options)`
  - Beide liefern entweder `""` oder einen Text, der mit `"\n\n"` beginnt (der Aufrufer hängt ihn
    roh an einen bestehenden Body an und fügt selbst keinen Abstand ein).

- [ ] **Step 1: Schalter anlegen**

In `src/Naudit.Core/Review/ReviewOptions.cs` in der Klasse `ReviewResolutionOptions` unter
`RenderCheckbox` ergänzen:

```csharp
    public bool RenderHint { get; set; } = true;           // Hinweis auf @naudit fp/ok am Kommentar
```

- [ ] **Step 2: Failing test schreiben**

Neue Datei `tests/Naudit.Tests/ReviewCommandHintTests.cs`:

```csharp
using Naudit.Core.Review;
using Naudit.Infrastructure.Git;
using Xunit;

namespace Naudit.Tests;

public class ReviewCommandHintTests
{
    [Fact]
    public void Inline_whenRenderHintOff_isEmpty()
    {
        var options = new ReviewResolutionOptions { RenderHint = false };

        Assert.Equal(string.Empty, ReviewCommandHint.Inline(options));
        Assert.Equal(string.Empty, ReviewCommandHint.Summary(options));
    }

    [Fact]
    public void Inline_isHiddenHtmlComment()
    {
        var hint = ReviewCommandHint.Inline(new ReviewResolutionOptions());

        Assert.StartsWith("\n\n<!--", hint);
        Assert.EndsWith("-->", hint);
        // Ein "--" im Inneren wuerde den Kommentar aufbrechen und den Rest sichtbar machen.
        var inner = hint[(hint.IndexOf("<!--", StringComparison.Ordinal) + 4)..^3];
        Assert.DoesNotContain("--", inner);
    }

    [Fact]
    public void Inline_whenResolutionDisabled_omitsOkCommand()
    {
        // @naudit ok wird bei ausgeschaltetem Resolution-Tracking still verworfen -> nicht bewerben.
        var hint = ReviewCommandHint.Inline(new ReviewResolutionOptions { Enabled = false });

        Assert.Contains("@naudit fp", hint);
        Assert.DoesNotContain("@naudit ok", hint);
    }

    [Fact]
    public void Summary_whenResolutionDisabled_omitsOkCommand()
    {
        var hint = ReviewCommandHint.Summary(new ReviewResolutionOptions { Enabled = false });

        Assert.Contains("@naudit fp", hint);
        Assert.DoesNotContain("@naudit ok", hint);
    }

    [Fact]
    public void Summary_isCollapsedDetailsBlock()
    {
        var hint = ReviewCommandHint.Summary(new ReviewResolutionOptions());

        Assert.Contains("<details>", hint);
        Assert.Contains("</details>", hint);
        Assert.Contains("@naudit ok", hint);
    }

    [Fact]
    public void Inline_commandLines_areParsedByFpReplyCommand()
    {
        // Kopplungs-Guard: Hinweistext und Parser duerfen nicht auseinanderlaufen.
        var hint = ReviewCommandHint.Inline(new ReviewResolutionOptions());

        var kinds = hint.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("@naudit", StringComparison.Ordinal))
            .Select(l => FpReplyCommand.TryParse(l))
            .ToList();

        Assert.Equal(2, kinds.Count);
        Assert.All(kinds, k => Assert.NotNull(k));
        Assert.Contains(kinds, k => k!.Kind == ReviewCommandKind.FalsePositive);
        Assert.Contains(kinds, k => k!.Kind == ReviewCommandKind.Accept);
    }
}
```

- [ ] **Step 3: Test laufen lassen, Fehlschlag prüfen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewCommandHintTests`
Expected: Build-Fehler `CS0103: The name 'ReviewCommandHint' does not exist` (bzw.
`CS0246`) — die Klasse gibt es noch nicht.

- [ ] **Step 4: Minimale Implementierung**

Neue Datei `src/Naudit.Core/Review/ReviewCommandHint.cs`:

```csharp
namespace Naudit.Core.Review;

/// <summary>Hinweis auf die Antwort-Kommandos (@naudit fp / @naudit ok), der an die GEPOSTETE
/// Kopie eines Reviews gehaengt wird. Inline als HTML-Kommentar: GitHub und GitLab verschlucken
/// den beim Rendern, liefern ihn aber im rohen Body der API aus — genau der Weg, auf dem ein
/// AI-Agent im Thread von den Kommandos erfaehrt, ohne dass ein Mensch Rauschen sieht. In der
/// Summary stattdessen ein zugeklappter details-Block, damit die Funktion einmal pro Review auch
/// fuer Menschen auffindbar bleibt.</summary>
public static class ReviewCommandHint
{
    /// <summary>Unsichtbarer Block fuer jeden Inline-Kommentar. Bewusst ASCII und ohne "--":
    /// ein doppelter Bindestrich im Inneren wuerde den HTML-Kommentar aufbrechen.</summary>
    public static string Inline(ReviewResolutionOptions options)
    {
        if (!options.RenderHint)
            return string.Empty;

        // Ohne Resolution-Tracking wird "ok" still verworfen — dann gar nicht erst nennen.
        var ok = options.Enabled
            ? "\n  @naudit ok <text>    - Finding angenommen/umgesetzt."
            : string.Empty;

        return "\n\n<!-- naudit:commands\n"
             + "Antworte AUF DIESEN KOMMENTAR (Reply im selben Thread, kein neuer Top-Level-Kommentar).\n"
             + "Erste Zeile der Antwort, genau eines:\n"
             + "  @naudit fp <grund>   - Fehlalarm; Naudit merkt sich das dauerhaft fuer dieses Projekt."
             + ok + "\n"
             + "Nur Repo-Mitglieder (Developer/Collaborator aufwaerts) sind autorisiert.\n"
             + "-->";
    }

    /// <summary>Zugeklappter Block fuer den Summary-Kommentar — dieselbe Information fuer Menschen.</summary>
    public static string Summary(ReviewResolutionOptions options)
    {
        if (!options.RenderHint)
            return string.Empty;

        var ok = options.Enabled
            ? "\n- `@naudit ok <text>` — Finding angenommen/umgesetzt."
            : string.Empty;

        return "\n\n<details><summary>🤖 Naudit-Kommandos</summary>\n\n"
             + "Antworte im Thread eines Inline-Kommentars — erste Zeile der Antwort:\n\n"
             + "- `@naudit fp <grund>` — Fehlalarm. Naudit merkt sich das für dieses Projekt und meidet den Fund künftig."
             + ok + "\n\n"
             + "Nur Repo-Mitglieder (Developer/Collaborator aufwärts) sind autorisiert. Ein neuer "
             + "Top-Level-Kommentar wird nicht ausgewertet, es muss eine Antwort auf den Kommentar sein.\n"
             + "</details>";
    }
}
```

- [ ] **Step 5: Tests laufen lassen, grün prüfen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewCommandHintTests`
Expected: PASS (6 Tests)

- [ ] **Step 6: Schlüssel in den Settings-Katalog aufnehmen**

In `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs` direkt nach der Zeile
`new("Naudit:Review:Resolution:RenderCheckbox", false),` ergänzen:

```csharp
        new("Naudit:Review:Resolution:RenderHint", false),
```

Damit ist der Schalter DB-verwaltet und erscheint automatisch in der Raw-keys-Ansicht der WebUI
(`src/frontend/src/components/settings/RawKeys.tsx` rendert jeden Katalog-Eintrag) — keine
Frontend-Änderung nötig.

- [ ] **Step 7: Volle Suite laufen lassen**

Run: `dotnet test Naudit.slnx`
Expected: PASS — insbesondere `SettingsCatalogTests`/`SettingsEndpointsTests`, falls sie die
Katalog-Größe prüfen.

- [ ] **Step 8: Commit**

```bash
git add src/Naudit.Core/Review/ReviewCommandHint.cs src/Naudit.Core/Review/ReviewOptions.cs \
        src/Naudit.Infrastructure/Settings/SettingsCatalog.cs tests/Naudit.Tests/ReviewCommandHintTests.cs
git commit -m "feat(review): Hinweis-Bausteine fuer @naudit fp/ok plus RenderHint-Schalter"
```

---

### Task 2: Hinweis an die gepostete Kopie hängen

**Files:**
- Modify: `src/Naudit.Core/Review/ReviewService.cs:123-128`
- Test: `tests/Naudit.Tests/ReviewServiceTests.cs`

**Interfaces:**
- Consumes: `ReviewCommandHint.Inline(ReviewResolutionOptions)` und
  `ReviewCommandHint.Summary(ReviewResolutionOptions)` aus Task 1; `options.Resolution`
  (`ReviewOptions.Resolution`, existiert).
- Produces: keine neuen öffentlichen Signaturen. Verhalten: der an
  `IGitPlatform.PostReviewAsync` übergebene `summaryMarkdown` und jeder `InlineComment.Body`
  tragen den Hinweis; `IReviewAuditSink`-Audit (`ReviewAudit.Summary`, `AuditFinding.Text`) und
  `ReviewResult.Markdown` bleiben ohne Hinweis.

- [ ] **Step 1: Failing tests schreiben**

Ans Ende von `tests/Naudit.Tests/ReviewServiceTests.cs` (vor der schließenden Klammer der Klasse):

```csharp
    [Fact]
    public async Task ReviewAsync_appendsCommandHint_toPostedCopyOnly()
    {
        // Der Hinweis gehoert an die gepostete Kopie — nicht in die DB: sonst schleppen
        // WebUI-Review-Detail und Analytics den Block in jedem Finding mit.
        var chat = new FakeChatClient(
            """{"summary":"## Review","comments":[{"file":"src/Foo.cs","line":1,"comment":"null deref","severity":"medium","confidence":"high"}]}""");
        var git = new FakeGitPlatform([new CodeChange("src/Foo.cs", "@@ -0,0 +1,1 @@\n+var x = foo();")]);
        var sink = new FakeReviewAuditSink();
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" }, auditSink: sink);

        var result = await service.ReviewAsync(Request);

        var inline = Assert.Single(git.PostedComments);
        Assert.Contains("naudit:commands", inline.Body);
        Assert.Contains("Naudit-Kommandos", git.PostedMarkdown!);

        var audit = Assert.Single(sink.Recorded);
        Assert.DoesNotContain("naudit:commands", Assert.Single(audit.Findings).Text);
        Assert.DoesNotContain("Naudit-Kommandos", audit.Summary);
        Assert.DoesNotContain("Naudit-Kommandos", result.Markdown);
    }

    [Fact]
    public async Task ReviewAsync_withRenderHintOff_postsNoHint()
    {
        var chat = new FakeChatClient(
            """{"summary":"## Review","comments":[{"file":"src/Foo.cs","line":1,"comment":"null deref"}]}""");
        var git = new FakeGitPlatform([new CodeChange("src/Foo.cs", "@@ -0,0 +1,1 @@\n+var x = foo();")]);
        var options = new ReviewOptions { SystemPrompt = "SYS" };
        options.Resolution.RenderHint = false;
        var service = CreateService(chat, git, options);

        await service.ReviewAsync(Request);

        Assert.DoesNotContain("naudit:commands", Assert.Single(git.PostedComments).Body);
        Assert.DoesNotContain("Naudit-Kommandos", git.PostedMarkdown!);
    }
```

- [ ] **Step 2: Tests laufen lassen, Fehlschlag prüfen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~ReviewAsync_appendsCommandHint_toPostedCopyOnly"`
Expected: FAIL — `Assert.Contains() Failure`, weil `naudit:commands` im geposteten Body fehlt.

- [ ] **Step 3: Minimale Implementierung**

In `src/Naudit.Core/Review/ReviewService.cs` den Block ab Zeile 125 (`var summary = …`) ersetzen:

```csharp
        var summary = ComposeSummary(parsed.Summary, verdict, inline.Count, orphans, lastRoundtrip);

        // Der Kommando-Hinweis haengt NUR an der geposteten Kopie: Audit-Bodies (und damit
        // WebUI-Review-Detail, Analytics und der spaetere LLM-Klassifikator) bleiben sauber.
        // postInline bleibt index-gleich zu inline, damit das Zippen der PostedComments traegt.
        var hint = ReviewCommandHint.Inline(options.Resolution);
        var postInline = hint.Length == 0
            ? inline
            : inline.Select(c => c with { Body = c.Body + hint }).ToList();
        var postSummary = summary + ReviewCommandHint.Summary(options.Resolution);

        var posted = await gitPlatform.PostReviewAsync(request, postSummary, postInline, verdict, ct);
        await RecordAuditAsync(request, verdict, summary, inline, orphans, posted, response, selection.UsedSessionAccountId(), ct);
        return new ReviewResult(summary, verdict);
```

Hinweis: `postInline` ist `List<InlineComment>` bzw. `List<InlineComment>` — beide Zweige passen
auf den `IReadOnlyList<InlineComment>`-Parameter, ggf. `IReadOnlyList<InlineComment> postInline =`
schreiben, falls der Compiler den Typ der ternären Zuweisung nicht vereint.

- [ ] **Step 4: Tests laufen lassen, grün prüfen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests`
Expected: PASS — auch die bestehenden Tests (`ReviewAsync_validLine_isPostedInline_withSeverityBadgeAndFields`
prüft mit `Assert.Contains`, angehängter Text stört nicht).

- [ ] **Step 5: Volle Suite laufen lassen**

Run: `dotnet test Naudit.slnx`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Core/Review/ReviewService.cs tests/Naudit.Tests/ReviewServiceTests.cs
git commit -m "feat(review): Kommando-Hinweis an die gepostete Review-Kopie haengen"
```

---

### Task 3: Dokumentation

**Files:**
- Modify: `docs/review-memory.md` (Abschnitt „Reply command: `@naudit fp` (PR 2b)", ab Zeile 194)
- Modify: `docs/review-analytics.md` (Abschnitt zum `@naudit ok`-Kommando, um Zeile 55)
- Modify: `docs/configuration.md` (Tabelle mit den `Naudit:Review:*`-Schlüsseln, um Zeile 150)
- Modify: `CLAUDE.md` (Abschnitt „Review analytics (PR 3 …)" / „Comment→finding mapping")

**Interfaces:**
- Consumes: das fertige Verhalten aus Task 1+2.
- Produces: nichts Ausführbares.

- [ ] **Step 1: `docs/review-memory.md` erweitern**

Am Ende des Abschnitts „Reply command: `@naudit fp` (PR 2b)" (nach dem Absatz „No new configuration
key and no migration …") anfügen:

```markdown
**Discoverability** — every posted review advertises the commands, so a human *or
an AI agent* working the PR can use them without prior knowledge:

- Each inline comment carries an HTML comment (`<!-- naudit:commands … -->`).
  GitHub and GitLab swallow it when rendering, but it is part of the raw body the
  APIs return (`gh api`, `gh pr view --comments`, GitLab Notes API) — invisible to
  humans, plainly readable for an agent that reads the thread through the API.
- The summary comment carries the same information as a collapsed
  `<details>` block, so the feature stays discoverable for humans exactly once per
  review.

Both are built by `ReviewCommandHint` (`src/Naudit.Core/Review/`) and appended by
`ReviewService` **only to the copy handed to `PostReviewAsync`** — the audit rows
(`ReviewEntity.Summary`, `ReviewFindingEntity.Body`) stay free of the boilerplate.
`@naudit ok` is omitted from the hint when `Naudit:Review:Resolution:Enabled` is
`false` (the command would be silently dropped). Turn the hint off entirely with
`Naudit:Review:Resolution:RenderHint=false`.
```

- [ ] **Step 2: `docs/review-analytics.md` ergänzen**

Im Abschnitt über das `@naudit ok`-Kommando (um Zeile 55) einen Satz anfügen:

```markdown
Both commands are advertised in every posted review — hidden in an HTML comment on
each inline comment (for API readers and AI agents) and in a collapsed `<details>`
block on the summary; see [Review memory › Discoverability](review-memory.md#reply-command-naudit-fp-pr-2b).
```

- [ ] **Step 3: `docs/configuration.md` ergänzen**

Direkt nach der Zeile für `Naudit:Review:Memory:MaxEntries` einfügen:

```markdown
| `Naudit:Review:Resolution:RenderHint` | Advertise the `@naudit fp` / `@naudit ok` reply commands in every posted review — hidden HTML comment inline, collapsed `<details>` on the summary. **Default `true`** (see [Review memory](review-memory.md)) |
```

- [ ] **Step 4: `CLAUDE.md` ergänzen**

Im Bullet „Review analytics (PR 3 …)" nach dem Satz über `@naudit ok` anfügen:

```markdown
  Beide Kommandos werden in jedem geposteten Review beworben (`ReviewCommandHint`,
  Core): unsichtbarer HTML-Kommentar an jedem Inline-Kommentar (für API-Leser/AI-Agents)
  plus zugeklappter `<details>`-Block an der Summary; angehängt **nur** an die gepostete
  Kopie, die Audit-Zeilen bleiben sauber. Schalter `Naudit:Review:Resolution:RenderHint`
  (Default `true`).
```

- [ ] **Step 5: Links prüfen**

Run: `grep -n "RenderHint" docs/*.md CLAUDE.md`
Expected: Treffer in `docs/configuration.md`, `docs/review-memory.md` und `CLAUDE.md`.

- [ ] **Step 6: Commit**

```bash
git add docs/review-memory.md docs/review-analytics.md docs/configuration.md CLAUDE.md
git commit -m "docs: Kommando-Hinweis im Review dokumentieren"
```

---

## Manuelle Abnahme (nach Task 3)

Nicht automatisiert testbar ist nur die Frage, ob die Plattform den HTML-Kommentar tatsächlich
verschluckt. Einmal an einem echten PR/MR prüfen:

1. Review auslösen, den Inline-Kommentar in der Weboberfläche ansehen → es darf **nichts** vom
   Hinweis zu sehen sein.
2. Denselben Kommentar über die API lesen (`gh api repos/<owner>/<repo>/pulls/comments/<id> --jq .body`
   bzw. GitLab `GET /projects/:id/merge_requests/:iid/notes`) → der Block `<!-- naudit:commands … -->`
   muss im Body stehen.
3. Mit `@naudit fp test` auf den Kommentar antworten → Bestätigung „Als False Positive gemerkt."
   erscheint im Thread.
