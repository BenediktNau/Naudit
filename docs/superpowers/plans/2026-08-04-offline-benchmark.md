# Offline-Benchmark (withmartian) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Naudit auf den 50 PRs des offline-Teils von `withmartian/code-review-benchmark` laufen
lassen, ohne irgendetwas nach GitHub zu schreiben, und die Ergebnisse so einspeisen, dass Naudit
neben den 41 bereits vermessenen Tools in Precision/Recall dasteht.

**Architecture:** Ein neues Konsolenprojekt `tools/Naudit.Benchmark` fährt die echte
DI-Registrierung hoch und tauscht **eine** Registrierung aus: `IGitPlatform` wird von
`CapturingGitPlatform` dekoriert — Lesen (`GetChangesAsync`, `GetCheckoutAsync`) geht an die echte
GitHub-Implementierung, `PostReviewAsync` schreibt in eine Datei statt zu posten. Ein
Python-Skript trägt die aufgefangenen Kommentare als Tool `naudit` in die
`benchmark_data.json` des Benchmarks ein; dessen Auswertungsschritte laufen danach unverändert.
`src/` wird **nicht** angefasst.

**Tech Stack:** C# / .NET 10, xUnit, Python 3 (Benchmark-Seite, `uv`). Keine neuen NuGet-Pakete,
keine Migration, keine Frontend-Änderung.

**Spec:** `docs/superpowers/specs/2026-08-04-code-review-benchmark-design.md`

> **Der Code im Repo ist maßgeblich, nicht die Blöcke in diesem Plan.** Die Reviews haben an
> mehreren Stellen Fehler in den hier ausgeschriebenen Entwürfen gefunden; der umgesetzte Stand
> weicht deshalb bewusst ab. Wer aus diesem Dokument reimplementiert, holt sich die behobenen
> Fehler zurück. Die wichtigsten Abweichungen:
>
> | Stelle | Im Plan unten | Umgesetzt |
> |---|---|---|
> | `ResultStore` (Task 3) | `File.WriteAllText`, ungeschützter `Deserialize`, `RemoveAll`+`Add` | atomar über Temp-Datei, korrupte Datei als `.corrupt` beiseite, positionserhaltendes Ersetzen |
> | `CapturingGitPlatform` (Task 1) | zählt den Checkout **vor** dem Aufruf | zählt Erfolg erst nach Rückkehr, Fehlschlag getrennt |
> | `ReviewDiagnostics` (Task 3) | zwei Werte | zusätzlich Kontext-/Guidelines-Nachweis, Token-Zahlen, Dateizahl, ausgecheckter Stand |
> | Diagnose-Quellen (Task 5) | nur Logger-Warnungen | zusätzlich ein `IChatClient`-Dekorator und ein `IWorkspaceProvider`-Dekorator |
> | `import_reviews.py` (Task 6) | schreibt direkt, akzeptiert Teilläufe | atomar über `os.replace`, verweigert unvollständige Läufe (`--allow-partial`) |
> | Umgebungsvariablen (Task 5) | handgebautes Einlesen | `Microsoft.Extensions.Configuration.EnvironmentVariables` |

## Global Constraints

- Solution-Datei ist `Naudit.slnx`, **nicht** `Naudit.sln`.
- Code-Kommentare auf Deutsch (Repo-Konvention).
- **`src/` bleibt unverändert.** Alles Neue liegt in `tools/` und `tests/`. Ein Task, der eine
  Datei unter `src/` anfasst, ist ein Fehler im Task.
- Core-Regel bleibt gewahrt: das neue Projekt referenziert `Naudit.Core` und
  `Naudit.Infrastructure`, aber Core selbst bekommt keine neue Abhängigkeit.
- **Keine neuen NuGet-Pakete — mit einer benannten Ausnahme.** In `tools/Naudit.Benchmark` ist
  `Microsoft.Extensions.Configuration.EnvironmentVariables` erlaubt (Erstanbieter, gehört zum
  Framework, geht nicht ins Container-Image). Grund: das Konsolen-SDK bringt es anders als das
  Web-SDK nicht mit, und der handgebaute Ersatz war fragil — zwei Umgebungsvariablen, die sich
  nur in der Groß-/Kleinschreibung unterscheiden, hätten das Werkzeug beim Start abstürzen
  lassen. Entschieden am 2026-08-04. Für `src/` gilt die Regel unverändert.
- `tools/Naudit.Benchmark` wird **nicht** ins `Dockerfile` aufgenommen — das Image baut weiter
  nur `src/Naudit.Web`.
- Volle Testsuite immer mit `DOTNET_USE_POLLING_FILE_WATCHER=1` laufen lassen (sonst kippen
  zufällig 2–7 Endpoint-Tests am inotify-Limit).
- Der Benchmark liegt als eigener Klon außerhalb dieses Repos. Pfad überall über die
  Umgebungsvariable `NAUDIT_BENCHMARK_REPO` (z. B. `~/workspace/code-review-benchmark`), nie
  hartkodiert.
- Der GitHub-Token für den Lauf ist **read-only**. Der Code ruft keinen Schreib-Endpunkt auf; der
  Token ist die zweite Absicherung, nicht die erste.

---

### Task 1: Konsolenprojekt + `CapturingGitPlatform`

Der Dekorator ist das Herzstück: er macht aus einem echten Review einen aufgefangenen, ohne die
Lesepfade anzufassen.

**Files:**
- Create: `tools/Naudit.Benchmark/Naudit.Benchmark.csproj`
- Create: `tools/Naudit.Benchmark/CapturingGitPlatform.cs`
- Create: `tools/Naudit.Benchmark/CapturedReview.cs`
- Modify: `Naudit.slnx`
- Modify: `tests/Naudit.Tests/Naudit.Tests.csproj` (ProjectReference auf das Tool-Projekt)
- Test: `tests/Naudit.Tests/BenchmarkCaptureTests.cs`

**Interfaces:**
- Consumes: `IGitPlatform`, `ReviewRequest`, `CodeChange`, `InlineComment`, `PostedComment`,
  `ReviewVerdict`, `RepoCheckoutInfo` aus `Naudit.Core`.
- Produces:
  - `sealed record CapturedComment(string FilePath, int NewLine, string Body, string Severity, string Confidence)`
  - `sealed record CapturedReview(string ProjectId, int MergeRequestIid, string Summary, string Verdict, IReadOnlyList<CapturedComment> Comments)`
  - `sealed class ReviewCapture` mit `void Record(ReviewRequest, string, IReadOnlyList<InlineComment>, ReviewVerdict)` und `CapturedReview? Last { get; }`
  - `sealed class CapturingGitPlatform(IGitPlatform inner, ReviewCapture capture) : IGitPlatform`

- [ ] **Step 1: Projektdatei anlegen**

`tools/Naudit.Benchmark/Naudit.Benchmark.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Naudit.Benchmark</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Naudit.Core\Naudit.Core.csproj" />
    <ProjectReference Include="..\..\src\Naudit.Infrastructure\Naudit.Infrastructure.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Projekt in die Solution aufnehmen**

`Naudit.slnx` — neuen Ordner `/tools/` ergänzen:

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/Naudit.Core/Naudit.Core.csproj" />
    <Project Path="src/Naudit.Infrastructure/Naudit.Infrastructure.csproj" />
    <Project Path="src/Naudit.Web/Naudit.Web.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/Naudit.Tests/Naudit.Tests.csproj" />
  </Folder>
  <Folder Name="/tools/">
    <Project Path="tools/Naudit.Benchmark/Naudit.Benchmark.csproj" />
  </Folder>
</Solution>
```

- [ ] **Step 3: Projektreferenz im Testprojekt ergänzen**

In `tests/Naudit.Tests/Naudit.Tests.csproj` in die bestehende `ItemGroup` mit den
ProjectReferences aufnehmen:

```xml
    <ProjectReference Include="..\..\tools\Naudit.Benchmark\Naudit.Benchmark.csproj" />
```

- [ ] **Step 4: Den fehlschlagenden Test schreiben**

`tests/Naudit.Tests/BenchmarkCaptureTests.cs`:

```csharp
using Naudit.Benchmark;
using Naudit.Core.Models;
using Naudit.Tests.Fakes;

namespace Naudit.Tests;

public class BenchmarkCaptureTests
{
    private static ReviewRequest Request() => new("getsentry/sentry", 93824, "Titel");

    [Fact]
    public async Task GetChangesAsync_delegiert_an_die_innere_Plattform()
    {
        var inner = new FakeGitPlatform([new CodeChange("a.cs", "@@ -1 +1 @@")]);
        var sut = new CapturingGitPlatform(inner, new ReviewCapture());

        var changes = await sut.GetChangesAsync(Request());

        Assert.Single(changes);
        Assert.Equal("a.cs", changes[0].FilePath);
    }

    [Fact]
    public async Task GetCheckoutAsync_delegiert_und_wird_mitgezaehlt()
    {
        var inner = new FakeGitPlatform([]);
        var capture = new ReviewCapture();
        var sut = new CapturingGitPlatform(inner, capture);

        var info = await sut.GetCheckoutAsync(Request());

        Assert.Equal("refs/test/head", info.HeadRef);
        // Der Zähler ist die einzige von außen sichtbare Spur, dass ein Checkout überhaupt
        // versucht wurde — Naudit schluckt Checkout-Fehler bewusst (fail-open).
        Assert.Equal(1, capture.CheckoutCalls);
    }

    [Fact]
    public void Reset_setzt_Aufzeichnung_und_Checkout_Zaehler_zurueck()
    {
        var capture = new ReviewCapture();
        capture.RecordCheckout();
        capture.Record(Request(), "s", [], ReviewVerdict.Approve);

        capture.Reset();

        Assert.Null(capture.Last);
        Assert.Equal(0, capture.CheckoutCalls);
    }

    [Fact]
    public async Task PostReviewAsync_postet_nicht_und_zeichnet_stattdessen_auf()
    {
        var inner = new FakeGitPlatform([]);
        var capture = new ReviewCapture();
        var sut = new CapturingGitPlatform(inner, capture);
        var comments = new[]
        {
            new InlineComment("a.cs", 12, null, "Fund A", FindingSeverity.High, ReviewConfidence.Medium),
        };

        await sut.PostReviewAsync(Request(), "Zusammenfassung", comments, ReviewVerdict.RequestChanges);

        // Nichts an die echte Plattform durchgereicht.
        Assert.Equal(0, inner.PostCallCount);

        var captured = capture.Last;
        Assert.NotNull(captured);
        Assert.Equal("getsentry/sentry", captured.ProjectId);
        Assert.Equal(93824, captured.MergeRequestIid);
        Assert.Equal("Zusammenfassung", captured.Summary);
        Assert.Equal("RequestChanges", captured.Verdict);
        var only = Assert.Single(captured.Comments);
        Assert.Equal("a.cs", only.FilePath);
        Assert.Equal(12, only.NewLine);
        Assert.Equal("Fund A", only.Body);
        Assert.Equal("High", only.Severity);
        Assert.Equal("Medium", only.Confidence);
    }

    [Fact]
    public async Task PostReviewAsync_liefert_indexgleiche_leere_Ids_zurueck()
    {
        // Vertrag von IGitPlatform: je Eingabe-Kommentar ein PostedComment, Ids dürfen null sein.
        var sut = new CapturingGitPlatform(new FakeGitPlatform([]), new ReviewCapture());
        var comments = new[]
        {
            new InlineComment("a.cs", 1, null, "A"),
            new InlineComment("b.cs", 2, null, "B"),
        };

        var posted = await sut.PostReviewAsync(Request(), "s", comments, ReviewVerdict.Approve);

        Assert.Equal(2, posted.Count);
        Assert.All(posted, p => Assert.Null(p.CommentId));
        Assert.All(posted, p => Assert.Null(p.NoteId));
    }
}
```

- [ ] **Step 5: Test laufen lassen und Fehlschlag bestätigen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter BenchmarkCaptureTests`
Expected: Kompilierfehler — `Naudit.Benchmark` bzw. `CapturingGitPlatform` existiert nicht.

- [ ] **Step 6: Datenmodell implementieren**

`tools/Naudit.Benchmark/CapturedReview.cs`:

```csharp
using Naudit.Core.Models;

namespace Naudit.Benchmark;

/// <summary>Ein aufgefangener Inline-Kommentar. Severity/Confidence als Text, damit die
/// JSON-Datei ohne Kenntnis der Core-Enums lesbar bleibt.</summary>
public sealed record CapturedComment(
    string FilePath, int NewLine, string Body, string Severity, string Confidence);

/// <summary>Ein vollständig aufgefangener Review — das, was sonst an die Plattform ginge.</summary>
public sealed record CapturedReview(
    string ProjectId, int MergeRequestIid, string Summary, string Verdict,
    IReadOnlyList<CapturedComment> Comments);

/// <summary>Sammelstelle für den Dekorator. Pro Prozess ein Review nach dem anderen —
/// der Runner läuft bewusst seriell, also genügt "der letzte".</summary>
public sealed class ReviewCapture
{
    public CapturedReview? Last { get; private set; }

    /// <summary>Wie oft GetCheckoutAsync angefragt wurde. 0 heißt: der Checkout wurde gar nicht
    /// erst versucht — dann lief das Review ohne Repo-Kontext und ohne Architektur-Profil.</summary>
    public int CheckoutCalls { get; private set; }

    public void RecordCheckout() => CheckoutCalls++;

    public void Record(ReviewRequest request, string summaryMarkdown,
        IReadOnlyList<InlineComment> comments, ReviewVerdict verdict)
        => Last = new CapturedReview(
            request.ProjectId,
            request.MergeRequestIid,
            summaryMarkdown,
            verdict.ToString(),
            comments.Select(c => new CapturedComment(
                c.FilePath, c.NewLine, c.Body, c.Severity.ToString(), c.Confidence.ToString())).ToList());

    public void Reset()
    {
        Last = null;
        CheckoutCalls = 0;
    }
}
```

- [ ] **Step 7: Dekorator implementieren**

`tools/Naudit.Benchmark/CapturingGitPlatform.cs`:

```csharp
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Benchmark;

/// <summary>Liest über die echte Plattform, fängt das Posten ab. Der einzige Grund, warum der
/// Benchmark ohne Schreibzugriff auf GitHub auskommt: Naudit sortiert Funde außerhalb des Diffs
/// bereits selbst aus (ReviewService), die aufgefangene Kommentarmenge ist deshalb dieselbe,
/// die auch gepostet würde.</summary>
public sealed class CapturingGitPlatform(IGitPlatform inner, ReviewCapture capture) : IGitPlatform
{
    public Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default)
        => inner.GetChangesAsync(request, ct);

    public Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        capture.RecordCheckout();
        return inner.GetCheckoutAsync(request, ct);
    }

    public Task<IReadOnlyList<PostedComment>> PostReviewAsync(ReviewRequest request, string summaryMarkdown,
        IReadOnlyList<InlineComment> comments, ReviewVerdict verdict, CancellationToken ct = default)
    {
        capture.Record(request, summaryMarkdown, comments, verdict);
        // Index-gleiche null-Ids: exakt der dokumentierte Best-Effort-Fall der echten Implementierung.
        IReadOnlyList<PostedComment> ids = comments.Select(_ => new PostedComment(null, null)).ToList();
        return Task.FromResult(ids);
    }
}
```

- [ ] **Step 8: Test laufen lassen und Erfolg bestätigen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter BenchmarkCaptureTests`
Expected: PASS (5 Tests)

- [ ] **Step 9: Committen**

```bash
git add tools/Naudit.Benchmark Naudit.slnx tests/Naudit.Tests/Naudit.Tests.csproj tests/Naudit.Tests/BenchmarkCaptureTests.cs
git commit -m "feat(benchmark): Konsolenprojekt + CapturingGitPlatform (Posten abfangen statt senden)"
```

---

### Task 2: Datensatz einlesen und PR-URLs auflösen

Die 50 Einträge sind nicht einheitlich: 35 zeigen auf Upstream-PRs, 15 auf die Org
`ai-code-review-evaluation`. Beide Formen sind gültig und werden gleich behandelt — der Parser
darf keine Annahme über den Owner treffen.

**Files:**
- Create: `tools/Naudit.Benchmark/GoldenDataset.cs`
- Test: `tests/Naudit.Tests/BenchmarkDatasetTests.cs`

**Interfaces:**
- Consumes: nichts aus Task 1.
- Produces:
  - `sealed record GoldenEntry(string Url, string PrTitle, string ProjectId, int Number)`
  - `static class GoldenDataset` mit
    `static GoldenEntry Parse(string url, string prTitle)` und
    `static IReadOnlyList<GoldenEntry> Load(string goldenCommentsDir)`

- [ ] **Step 1: Den fehlschlagenden Test schreiben**

`tests/Naudit.Tests/BenchmarkDatasetTests.cs`:

```csharp
using Naudit.Benchmark;

namespace Naudit.Tests;

public class BenchmarkDatasetTests
{
    [Theory]
    // Upstream-PR (35 der 50 Einträge)
    [InlineData("https://github.com/getsentry/sentry/pull/93824", "getsentry/sentry", 93824)]
    [InlineData("https://github.com/calcom/cal.com/pull/21437", "calcom/cal.com", 21437)]
    [InlineData("https://github.com/grafana/grafana/pull/105892", "grafana/grafana", 105892)]
    // Vorbereitungs-Org (15 der 50 Einträge) — Punkt/Bindestrich im Repo-Namen, kleine Nummern
    [InlineData("https://github.com/ai-code-review-evaluation/discourse-graphite/pull/1",
        "ai-code-review-evaluation/discourse-graphite", 1)]
    [InlineData("https://github.com/ai-code-review-evaluation/sentry-greptile/pull/5",
        "ai-code-review-evaluation/sentry-greptile", 5)]
    public void Parse_liest_Projekt_und_Nummer_aus_beiden_URL_Formen(string url, string projectId, int number)
    {
        var entry = GoldenDataset.Parse(url, "Titel");

        Assert.Equal(projectId, entry.ProjectId);
        Assert.Equal(number, entry.Number);
        Assert.Equal(url, entry.Url);
        Assert.Equal("Titel", entry.PrTitle);
    }

    [Theory]
    [InlineData("https://github.com/discourse/discourse/commit/ffbaf8c5")]   // Commit, kein PR
    [InlineData("https://github.com/getsentry/sentry/pull/")]                 // Nummer fehlt
    [InlineData("https://example.com/getsentry/sentry/pull/1")]               // fremder Host
    [InlineData("")]
    public void Parse_wirft_bei_unbrauchbarer_URL(string url)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GoldenDataset.Parse(url, "Titel"));
        Assert.Contains(url, ex.Message);
    }

    [Fact]
    public void Load_liest_alle_Eintraege_aus_allen_JSON_Dateien()
    {
        var dir = Directory.CreateTempSubdirectory("naudit-golden-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "sentry.json"), """
            [
              {"pr_title": "Erster",  "url": "https://github.com/getsentry/sentry/pull/1", "comments": []},
              {"pr_title": "Zweiter", "url": "https://github.com/getsentry/sentry/pull/2", "comments": []}
            ]
            """);
            File.WriteAllText(Path.Combine(dir.FullName, "discourse.json"), """
            [
              {"pr_title": "Dritter",
               "url": "https://github.com/ai-code-review-evaluation/discourse-graphite/pull/3",
               "comments": []}
            ]
            """);

            var entries = GoldenDataset.Load(dir.FullName);

            Assert.Equal(3, entries.Count);
            Assert.Contains(entries, e => e.ProjectId == "ai-code-review-evaluation/discourse-graphite" && e.Number == 3);
            Assert.Contains(entries, e => e.PrTitle == "Erster");
        }
        finally { dir.Delete(recursive: true); }
    }
}
```

- [ ] **Step 2: Test laufen lassen und Fehlschlag bestätigen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter BenchmarkDatasetTests`
Expected: Kompilierfehler — `GoldenDataset` existiert nicht.

- [ ] **Step 3: Implementieren**

`tools/Naudit.Benchmark/GoldenDataset.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Naudit.Benchmark;

/// <summary>Ein Eintrag des Benchmark-Datensatzes: der zu reviewende PR.</summary>
public sealed record GoldenEntry(string Url, string PrTitle, string ProjectId, int Number);

/// <summary>Liest golden_comments/*.json. Maßgeblich ist das Feld "url" — 35 der 50 Einträge
/// zeigen auf den Upstream-PR, 15 auf vorbereitete PRs in der Org ai-code-review-evaluation
/// (für die es gar keinen Upstream-PR gibt). Der Originalweg klont ebenfalls, was in "url" steht;
/// nur so reviewt Naudit dieselbe Vorlage wie die Vergleichstools.</summary>
public static class GoldenDataset
{
    private static readonly Regex PullUrl = new(
        @"^https://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/pull/(?<number>\d+)/?$",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static GoldenEntry Parse(string url, string prTitle)
    {
        var m = PullUrl.Match(url ?? string.Empty);
        if (!m.Success)
            throw new InvalidOperationException(
                $"Keine auswertbare GitHub-PR-URL: '{url}'. Erwartet: https://github.com/<owner>/<repo>/pull/<nummer>");

        return new GoldenEntry(url!, prTitle,
            $"{m.Groups["owner"].Value}/{m.Groups["repo"].Value}",
            int.Parse(m.Groups["number"].Value));
    }

    public static IReadOnlyList<GoldenEntry> Load(string goldenCommentsDir)
    {
        var entries = new List<GoldenEntry>();
        foreach (var file in Directory.EnumerateFiles(goldenCommentsDir, "*.json").OrderBy(f => f))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                var title = item.TryGetProperty("pr_title", out var t) ? t.GetString() : null;
                entries.Add(Parse(url ?? string.Empty, title ?? string.Empty));
            }
        }
        return entries;
    }
}
```

- [ ] **Step 4: Test laufen lassen und Erfolg bestätigen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter BenchmarkDatasetTests`
Expected: PASS (10 Tests)

- [ ] **Step 5: Committen**

```bash
git add tools/Naudit.Benchmark/GoldenDataset.cs tests/Naudit.Tests/BenchmarkDatasetTests.cs
git commit -m "feat(benchmark): golden_comments einlesen, beide PR-URL-Formen auflösen"
```

---

### Task 3: Ergebnisdatei, Wiederaufsetzen und Diagnose

Der Lauf dauert Stunden und teilt sich das Abo-Kontingent mit nichts anderem — er muss
unterbrechbar sein. Zusätzlich braucht er eine Diagnose, weil Naudits Pipeline bewusst fail-open
ist: ein Review ohne Checkout oder ohne Architektur-Profil ist stumm schlechter und darf nicht
unbemerkt in die Auswertung.

> **Nachtrag aus dem Review (Commit `89289f3`):** Der unten ausgeschriebene `ResultStore` war an
> zwei Stellen zu naiv für seinen eigenen Zweck. `File.WriteAllText` ist nicht atomar — ein
> Abbruch mitten im Schreiben hinterlässt abgeschnittenes JSON, und der Konstruktor ließ die
> daraus folgende `JsonException` durch, womit ein Absturz **alle** erledigten Einträge unlesbar
> machte. Ausserdem hängte `RemoveAll` + `Add` einen wiederholten Eintrag hinten an, statt ihn an
> Ort und Stelle zu ersetzen. Der umgesetzte Stand schreibt über eine temporäre Datei plus
> `File.Move(overwrite: true)`, legt eine korrupte Datei als `.corrupt` beiseite und startet leer,
> und ersetzt positionserhaltend. Maßgeblich ist der Code im Repo, nicht der Block hier.

**Files:**
- Create: `tools/Naudit.Benchmark/ResultStore.cs`
- Test: `tests/Naudit.Tests/BenchmarkResultStoreTests.cs`

**Interfaces:**
- Consumes: `CapturedReview` aus Task 1.
- Produces:
  - `sealed record ReviewDiagnostics(bool CheckoutRequested, IReadOnlyList<string> Warnings, double DurationSeconds, string? Error)`
  - `sealed record BenchmarkRecord(string Url, CapturedReview Review, ReviewDiagnostics Diagnostics)`
  - `sealed class ResultStore(string path)` mit
    `IReadOnlyCollection<string> CompletedUrls { get; }`,
    `void Append(BenchmarkRecord record)`,
    `IReadOnlyList<BenchmarkRecord> All()`

- [ ] **Step 1: Den fehlschlagenden Test schreiben**

`tests/Naudit.Tests/BenchmarkResultStoreTests.cs`:

```csharp
using Naudit.Benchmark;

namespace Naudit.Tests;

public class BenchmarkResultStoreTests
{
    private static BenchmarkRecord Record(string url, int number) => new(
        url,
        new CapturedReview("getsentry/sentry", number, "Zusammenfassung", "Approve",
            [new CapturedComment("a.cs", 5, "Fund", "High", "Medium")]),
        new ReviewDiagnostics(CheckoutRequested: true, Warnings: [], DurationSeconds: 12.5, Error: null));

    [Fact]
    public void CompletedUrls_ist_leer_wenn_die_Datei_noch_nicht_existiert()
    {
        var dir = Directory.CreateTempSubdirectory("naudit-store-");
        try
        {
            var store = new ResultStore(Path.Combine(dir.FullName, "naudit-reviews.json"));
            Assert.Empty(store.CompletedUrls);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Append_schreibt_sofort_und_ein_neuer_Store_liest_es_wieder()
    {
        var dir = Directory.CreateTempSubdirectory("naudit-store-");
        try
        {
            var path = Path.Combine(dir.FullName, "naudit-reviews.json");
            var first = new ResultStore(path);
            first.Append(Record("https://github.com/getsentry/sentry/pull/1", 1));
            first.Append(Record("https://github.com/getsentry/sentry/pull/2", 2));

            // Neuer Store = neuer Prozessstart nach Abbruch.
            var second = new ResultStore(path);

            Assert.Equal(2, second.CompletedUrls.Count);
            Assert.Contains("https://github.com/getsentry/sentry/pull/1", second.CompletedUrls);
            var all = second.All();
            Assert.Equal(2, all.Count);
            Assert.Equal("Zusammenfassung", all[0].Review.Summary);
            Assert.True(all[0].Diagnostics.CheckoutRequested);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Append_ersetzt_einen_vorhandenen_Eintrag_derselben_URL()
    {
        var dir = Directory.CreateTempSubdirectory("naudit-store-");
        try
        {
            var path = Path.Combine(dir.FullName, "naudit-reviews.json");
            var store = new ResultStore(path);
            store.Append(Record("https://github.com/getsentry/sentry/pull/1", 1));
            store.Append(Record("https://github.com/getsentry/sentry/pull/1", 1));

            Assert.Single(store.All());
        }
        finally { dir.Delete(recursive: true); }
    }
}
```

- [ ] **Step 2: Test laufen lassen und Fehlschlag bestätigen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter BenchmarkResultStoreTests`
Expected: Kompilierfehler — `ResultStore` existiert nicht.

- [ ] **Step 3: Implementieren**

`tools/Naudit.Benchmark/ResultStore.cs`:

```csharp
using System.Text.Json;

namespace Naudit.Benchmark;

/// <summary>Nachweis, dass ein Review unter vollen Bedingungen lief. Naudit ist fail-open:
/// ein fehlgeschlagener Checkout, eine gescheiterte Profil-Destillation oder ein toter Analyzer
/// ergeben still ein schlechteres Review. Von außen sind zwei Spuren beobachtbar — ob der
/// Checkout überhaupt angefragt wurde (Dekorator) und was die Pipeline währenddessen als
/// Warning/Error geloggt hat. Beides zusammen fängt die fail-open-Pfade ab, die sich melden.
/// Auffällige Läufe werden am Ende berichtet und wiederholt, nicht importiert.</summary>
public sealed record ReviewDiagnostics(
    bool CheckoutRequested, IReadOnlyList<string> Warnings, double DurationSeconds, string? Error);

/// <summary>Ein Datensatz je PR: was Naudit gesagt hätte, plus unter welchen Bedingungen.</summary>
public sealed record BenchmarkRecord(string Url, CapturedReview Review, ReviewDiagnostics Diagnostics);

/// <summary>Ergebnisdatei und zugleich Wiederaufsetzpunkt. Nach jedem Review neu geschrieben —
/// der Lauf dauert Stunden, ein Abbruch darf nichts kosten.</summary>
public sealed class ResultStore
{
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly List<BenchmarkRecord> _records;

    public ResultStore(string path)
    {
        _path = path;
        _records = File.Exists(path)
            ? JsonSerializer.Deserialize<List<BenchmarkRecord>>(File.ReadAllText(path), JsonOpts) ?? []
            : [];
    }

    public IReadOnlyCollection<string> CompletedUrls => _records.Select(r => r.Url).ToHashSet();

    public IReadOnlyList<BenchmarkRecord> All() => _records;

    public void Append(BenchmarkRecord record)
    {
        _records.RemoveAll(r => r.Url == record.Url);
        _records.Add(record);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_records, JsonOpts));
    }
}
```

- [ ] **Step 4: Test laufen lassen und Erfolg bestätigen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter BenchmarkResultStoreTests`
Expected: PASS (3 Tests)

- [ ] **Step 5: Committen**

```bash
git add tools/Naudit.Benchmark/ResultStore.cs tests/Naudit.Tests/BenchmarkResultStoreTests.cs
git commit -m "feat(benchmark): Ergebnisdatei mit Wiederaufsetzen und Fail-open-Diagnose"
```

---

### Task 4: Host-Verdrahtung mit ausgetauschtem `IGitPlatform`

`IGitPlatform` ist über `AddHttpClient<IGitPlatform, GitHubPlatform>` registriert — ein
Typed-Client, also eine Registrierung mit `ImplementationFactory`. Ohne Scrutor wird dekoriert,
indem man den vorhandenen Deskriptor entfernt und durch eine Fabrik ersetzt, die die
ursprüngliche Fabrik aufruft und das Ergebnis umhüllt. Der Wiring-Test beweist, dass der Tausch
greift — sonst würde der Benchmark unbemerkt echt posten.

**Files:**
- Create: `tools/Naudit.Benchmark/BenchmarkHost.cs`
- Test: `tests/Naudit.Tests/BenchmarkWiringTests.cs`

**Interfaces:**
- Consumes: `CapturingGitPlatform`, `ReviewCapture` (Task 1).
- Produces: `static class BenchmarkHost` mit
  `static IServiceCollection AddBenchmarkCapture(this IServiceCollection services)`

- [ ] **Step 1: Den fehlschlagenden Test schreiben**

`tests/Naudit.Tests/BenchmarkWiringTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Benchmark;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;

namespace Naudit.Tests;

public class BenchmarkWiringTests
{
    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Naudit:Git:Platform"] = "GitHub",
            ["Naudit:GitHub:Token"] = "test-token",
            ["Naudit:GitHub:WebhookSecret"] = "test-secret",
            ["Naudit:Ai:Provider"] = "ClaudeCode",
            ["Naudit:Ai:Model"] = "opus",
            ["Naudit:Db:ConnectionString"] = "Data Source=:memory:",
        })
        .Build();

    [Fact]
    public void AddBenchmarkCapture_ersetzt_IGitPlatform_durch_den_Dekorator()
    {
        var services = new ServiceCollection();
        var config = Config();
        services.AddSingleton(config);
        services.AddNauditDatabase(config);
        services.AddNauditInfrastructure(config);
        services.AddBenchmarkCapture();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var platform = scope.ServiceProvider.GetRequiredService<IGitPlatform>();

        Assert.IsType<CapturingGitPlatform>(platform);
    }

    [Fact]
    public void AddBenchmarkCapture_registriert_ReviewCapture_als_Singleton()
    {
        var services = new ServiceCollection();
        var config = Config();
        services.AddSingleton(config);
        services.AddNauditDatabase(config);
        services.AddNauditInfrastructure(config);
        services.AddBenchmarkCapture();

        using var provider = services.BuildServiceProvider();

        var a = provider.GetRequiredService<ReviewCapture>();
        var b = provider.GetRequiredService<ReviewCapture>();

        Assert.Same(a, b);
    }
}
```

- [ ] **Step 2: Test laufen lassen und Fehlschlag bestätigen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter BenchmarkWiringTests`
Expected: Kompilierfehler — `AddBenchmarkCapture` existiert nicht.

- [ ] **Step 3: Implementieren**

`tools/Naudit.Benchmark/BenchmarkHost.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;

namespace Naudit.Benchmark;

public static class BenchmarkHost
{
    /// <summary>Tauscht die zuletzt registrierte IGitPlatform gegen den aufzeichnenden Dekorator.
    /// Muss NACH AddNauditInfrastructure laufen. Die echte Registrierung ist ein Typed-HttpClient,
    /// hat also eine ImplementationFactory — die rufen wir auf und umhüllen das Ergebnis.</summary>
    public static IServiceCollection AddBenchmarkCapture(this IServiceCollection services)
    {
        services.AddSingleton<ReviewCapture>();

        var existing = services.LastOrDefault(d => d.ServiceType == typeof(IGitPlatform))
            ?? throw new InvalidOperationException(
                "Keine IGitPlatform-Registrierung gefunden — AddBenchmarkCapture muss nach AddNauditInfrastructure laufen.");

        if (existing.ImplementationFactory is null)
            throw new InvalidOperationException(
                "IGitPlatform ist nicht über eine Fabrik registriert — die Dekoration müsste angepasst werden.");

        services.Remove(existing);
        services.Add(new ServiceDescriptor(
            typeof(IGitPlatform),
            sp => new CapturingGitPlatform(
                (IGitPlatform)existing.ImplementationFactory(sp),
                sp.GetRequiredService<ReviewCapture>()),
            existing.Lifetime));

        return services;
    }
}
```

- [ ] **Step 4: Test laufen lassen und Erfolg bestätigen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter BenchmarkWiringTests`
Expected: PASS (2 Tests)

- [ ] **Step 5: Volle Suite laufen lassen**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS — keine Regression durch das neue Projekt in der Solution.

- [ ] **Step 6: Committen**

```bash
git add tools/Naudit.Benchmark/BenchmarkHost.cs tests/Naudit.Tests/BenchmarkWiringTests.cs
git commit -m "feat(benchmark): IGitPlatform-Dekoration verdrahten + Wiring-Test"
```

---

### Task 5: Der Runner

Serieller Lauf über alle Einträge, mit Pausen fürs Abo-Kontingent, Wiederaufsetzen und
Abschlussbericht über auffällige Reviews.

**Files:**
- Create: `tools/Naudit.Benchmark/WarningCollector.cs`
- Create: `tools/Naudit.Benchmark/Program.cs`
- Create: `tools/Naudit.Benchmark/README.md`

**Interfaces:**
- Consumes: `GoldenDataset`, `ResultStore`, `BenchmarkHost.AddBenchmarkCapture`, `ReviewCapture`.
- Produces: `sealed class WarningCollector` mit `IReadOnlyList<string> Drain()`; ausführbares Kommando.

- [ ] **Step 1: Warnungs-Sammler implementieren**

`tools/Naudit.Benchmark/WarningCollector.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Naudit.Benchmark;

/// <summary>Sammelt Warnings/Errors der Review-Pipeline — einer von drei Wegen, Naudits fail-open-
/// Pfade sichtbar zu machen. Er deckt die Stellen ab, die ihre Fehler zwar schlucken, aber loggen:
/// die git-Unterprozesse des GitWorkspaceProvider, die Guidelines-Destillation, das Review-
/// Gedächtnis und die SAST-Analyzer.
///
/// <para>Er deckt bewusst NICHT alles ab, und das ist der Grund für die übrigen Diagnosewerte:
/// GitHubPlatform.GetCheckoutAsync wirft ungeloggt (⇒ CheckoutFailed am IGitPlatform-Dekorator),
/// der WorkspaceContextCollector hat nicht einmal einen Logger (⇒ ContextInPrompt am
/// IChatClient-Dekorator), und die Audit-Senke meldet Fehler überhaupt nicht —
/// ReviewService.RecordAuditAsync schluckt ohne Log, EfReviewAuditSink loggt nur den
/// Erfolgsfall.</para></summary>
public sealed class WarningCollector
{
    private readonly List<string> _messages = [];
    private readonly Lock _gate = new();

    public void Add(string message)
    {
        lock (_gate) _messages.Add(message);
    }

    /// <summary>Liefert das Gesammelte und leert den Puffer — einmal pro Review aufgerufen.</summary>
    public IReadOnlyList<string> Drain()
    {
        lock (_gate)
        {
            var copy = _messages.ToArray();
            _messages.Clear();
            return copy;
        }
    }
}

public sealed class CollectingLoggerProvider(WarningCollector collector) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CollectingLogger(categoryName, collector);
    public void Dispose() { }

    private sealed class CollectingLogger(string category, WarningCollector collector) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = formatter(state, exception);
            collector.Add($"{logLevel}: {category}: {text}");
        }
    }
}
```

- [ ] **Step 2: Programm implementieren**

`tools/Naudit.Benchmark/Program.cs`:

```csharp
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Naudit.Benchmark;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Data;

// Pflichtangaben: Klon des Benchmarks + Ausgabedatei. Optionale Begrenzung für den Smoke-Test.
var benchmarkRepo = Environment.GetEnvironmentVariable("NAUDIT_BENCHMARK_REPO")
    ?? throw new InvalidOperationException("NAUDIT_BENCHMARK_REPO muss auf den Benchmark-Klon zeigen.");
var goldenDir = Path.Combine(benchmarkRepo, "offline", "golden_comments");
var outputPath = Environment.GetEnvironmentVariable("NAUDIT_BENCHMARK_OUTPUT")
    ?? Path.Combine(benchmarkRepo, "offline", "results", "naudit-reviews.json");
var limit = int.TryParse(Environment.GetEnvironmentVariable("NAUDIT_BENCHMARK_LIMIT"), out var l) ? l : int.MaxValue;
var pause = TimeSpan.FromSeconds(
    int.TryParse(Environment.GetEnvironmentVariable("NAUDIT_BENCHMARK_PAUSE_SECONDS"), out var p) ? p : 20);

var config = new ConfigurationBuilder()
    .AddJsonFile("benchmark.appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var warnings = new WarningCollector();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(config);
services.AddSingleton(warnings);
// Nur der Sammler — kein Konsolen-Provider (spart das Paket Microsoft.Extensions.Logging.Console;
// der Runner gibt Warnungen ohnehin selbst je Review aus).
services.AddLogging(b =>
{
    b.SetMinimumLevel(LogLevel.Warning);
    b.AddProvider(new CollectingLoggerProvider(warnings));
});
services.AddNauditDatabase(config);
services.AddNauditInfrastructure(config);
services.AddBenchmarkCapture();

using var provider = services.BuildServiceProvider();

// Schema anlegen. Im Web-Host erledigt das der DbSettingsLoader vor dem Host-Bau; hier gibt es
// ihn nicht. Ohne Migration scheiterte JEDER DB-Zugriff der Pipeline. Die Audit-Senke bliebe dabei
// stumm (ReviewService.RecordAuditAsync schluckt ohne Log, EfReviewAuditSink loggt nur den
// Erfolgsfall) — sichtbar würde es über die DB-Pfade von Review-Gedächtnis (DbReviewMemory) und
// Architektur-Profil (DistillingReviewGuidelines), die ihre Fehler beide als Warning loggen und
// damit über den WarningCollector alle 50 Reviews als auffällig melden.
using (var migrationScope = provider.CreateScope())
    await migrationScope.ServiceProvider.GetRequiredService<NauditDbContext>().Database.MigrateAsync();

// Preflight: erst alles parsen, dann erst reviewen — ein Tippfehler im Datensatz soll
// nicht nach dreißig Reviews auffallen.
var entries = GoldenDataset.Load(goldenDir);
Console.WriteLine($"{entries.Count} Einträge geladen, {entries.Select(e => e.ProjectId).Distinct().Count()} Projekte.");

var store = new ResultStore(outputPath);
var done = store.CompletedUrls;
var todo = entries.Where(e => !done.Contains(e.Url)).Take(limit).ToList();
Console.WriteLine($"{done.Count} bereits erledigt, {todo.Count} zu tun.");

var capture = provider.GetRequiredService<ReviewCapture>();
var index = 0;

foreach (var entry in todo)
{
    index++;
    Console.WriteLine($"[{index}/{todo.Count}] {entry.ProjectId}#{entry.Number} — {entry.PrTitle}");
    capture.Reset();
    warnings.Drain();   // Reste des Vorgängers verwerfen

    var sw = Stopwatch.StartNew();
    string? error = null;
    try
    {
        using var scope = provider.CreateScope();
        var reviewService = scope.ServiceProvider.GetRequiredService<ReviewService>();
        // Trigger = Ci: das Roundtrip-Limit ist hier bedeutungslos, aber die Absicht soll im Code stehen.
        var request = new ReviewRequest(entry.ProjectId, entry.Number, entry.PrTitle, null, ReviewTrigger.Ci);
        await reviewService.ReviewAsync(request);
    }
    catch (Exception ex)
    {
        error = ex.Message;
    }
    sw.Stop();

    var collected = warnings.Drain();
    var captured = capture.Last;
    if (captured is null)
    {
        // Kein PostReviewAsync ⇒ kein Review. Nicht speichern, damit der nächste Lauf es wiederholt.
        Console.WriteLine($"    FEHLGESCHLAGEN: {error ?? "kein Review erzeugt (leerer Diff?)"}");
        continue;
    }

    var diagnostics = new ReviewDiagnostics(
        CheckoutRequested: capture.CheckoutCalls > 0,
        Warnings: collected,
        DurationSeconds: sw.Elapsed.TotalSeconds,
        Error: error);

    store.Append(new BenchmarkRecord(entry.Url, captured, diagnostics));
    Console.WriteLine($"    {captured.Comments.Count} Inline-Kommentare, {captured.Verdict}, {sw.Elapsed.TotalSeconds:F0}s");
    if (!diagnostics.CheckoutRequested)
        Console.WriteLine("    ACHTUNG: kein Checkout angefragt — Review lief ohne Repo-Kontext.");
    foreach (var w in collected)
        Console.WriteLine($"    WARNUNG: {w}");

    if (index < todo.Count)
        await Task.Delay(pause);
}

// Abschlussbericht: was noch fehlt und was auffällig war.
var remaining = entries.Count - store.CompletedUrls.Count;
var suspicious = store.All()
    .Where(r => r.Diagnostics.Error is not null
             || !r.Diagnostics.CheckoutRequested
             || r.Diagnostics.Warnings.Count > 0)
    .ToList();
Console.WriteLine();
Console.WriteLine($"Fertig: {store.CompletedUrls.Count}/{entries.Count}, offen: {remaining}");
if (suspicious.Count > 0)
{
    Console.WriteLine($"ACHTUNG — {suspicious.Count} auffällige Reviews (vor dem Import wiederholen):");
    foreach (var r in suspicious)
    {
        var reason = r.Diagnostics.Error
            ?? (!r.Diagnostics.CheckoutRequested ? "kein Checkout angefragt"
                : string.Join(" | ", r.Diagnostics.Warnings));
        Console.WriteLine($"  {r.Url}: {reason}");
    }
}
```

- [ ] **Step 3: Benchmark klonen und Preflight ohne Reviews prüfen**

Der Klon ist Voraussetzung für diesen Schritt und für alle Tasks danach; er liegt bewusst
außerhalb dieses Repos.

```bash
git clone https://github.com/withmartian/code-review-benchmark.git ~/workspace/code-review-benchmark

export NAUDIT_BENCHMARK_REPO=~/workspace/code-review-benchmark
export NAUDIT_BENCHMARK_LIMIT=0
dotnet run --project tools/Naudit.Benchmark
```

Expected: Ausgabe `50 Einträge geladen, 7 Projekte.` und `0 zu tun.` — beweist ohne einen
einzigen LLM-Aufruf, dass alle 50 URLs auflösbar sind (die Spec verlangt genau diese Prüfung über
den vollen Datensatz; sie läuft hier zur Laufzeit statt als xUnit-Test, damit keine Kopie der
Benchmark-Daten ins Repo wandert).

- [ ] **Step 4: README schreiben**

`tools/Naudit.Benchmark/README.md`:

````markdown
# Naudit.Benchmark

Fährt Naudit über die 50 PRs des offline-Teils von `withmartian/code-review-benchmark` und
fängt die Review-Kommentare ab, statt sie zu posten.

> **Dieses Werkzeug schreibt nichts nach GitHub.** `PostReviewAsync` ist durch
> `CapturingGitPlatform` ersetzt; gelesen wird über die echte GitHub-Anbindung. Der Token für
> den Lauf sollte trotzdem read-only sein — zwei Schlösser sind besser als eines.

## Voraussetzungen

1. `claude` installiert und angemeldet, Token via `claude setup-token`.
2. Read-only GitHub-Token (öffentliche Repos genügen).
3. Klon des Benchmarks, z. B. nach `~/workspace/code-review-benchmark`.

## Umgebungsvariablen

| Variable | Bedeutung |
|---|---|
| `NAUDIT_BENCHMARK_REPO` | Pfad zum Benchmark-Klon (Pflicht) |
| `NAUDIT_BENCHMARK_OUTPUT` | Ergebnisdatei, Default `<repo>/offline/results/naudit-reviews.json` |
| `NAUDIT_BENCHMARK_LIMIT` | Anzahl Reviews in diesem Lauf (Default: alle; `1` für den Smoke-Test, `0` für reinen Preflight) |
| `NAUDIT_BENCHMARK_PAUSE_SECONDS` | Pause zwischen Reviews, Default 20 (Abo-Kontingent) |

Naudits eigene Konfiguration kommt wie gewohnt über `Naudit__*`-Variablen — siehe den
Implementierungsplan, Task 8.

## Ablauf

```bash
NAUDIT_BENCHMARK_LIMIT=0 dotnet run --project tools/Naudit.Benchmark   # Preflight
NAUDIT_BENCHMARK_LIMIT=1 dotnet run --project tools/Naudit.Benchmark   # Smoke-Test
dotnet run --project tools/Naudit.Benchmark                            # Vollauf
```

Der Lauf ist unterbrechbar: erledigte PRs stehen in der Ergebnisdatei und werden beim nächsten
Start übersprungen. Am Ende meldet das Werkzeug auffällige Reviews (Fehler, fehlender Checkout,
Warnungen aus der Pipeline) — die gehören wiederholt, **nicht** importiert, sonst zählt ein
stumm degradiertes Review als „nichts gefunden".

Import und Auswertung danach: `tools/benchmark/import_reviews.py` bzw. Task 9 des Plans.
````

- [ ] **Step 5: Committen**

```bash
git add tools/Naudit.Benchmark/WarningCollector.cs tools/Naudit.Benchmark/Program.cs tools/Naudit.Benchmark/README.md
git commit -m "feat(benchmark): serieller Runner mit Wiederaufsetzen, Pacing und Fail-open-Bericht"
```

---

### Task 6: Import in `benchmark_data.json`

**Files:**
- Create: `tools/benchmark/import_reviews.py`
- Create: `tools/benchmark/test_import_reviews.py`

**Interfaces:**
- Consumes: `naudit-reviews.json` aus Task 3/5.
- Produces: Funktion `build_review_entry(record) -> dict` und CLI
  `python import_reviews.py --reviews <pfad> --benchmark-data <pfad> [--force]`

- [ ] **Step 1: Den fehlschlagenden Test schreiben**

`tools/benchmark/test_import_reviews.py`:

```python
import json
import pytest
from import_reviews import build_review_entry, merge


def record(url="https://github.com/getsentry/sentry/pull/1"):
    return {
        "url": url,
        "review": {
            "projectId": "getsentry/sentry",
            "mergeRequestIid": 1,
            "summary": "Zusammenfassung",
            "verdict": "Approve",
            "comments": [
                {"filePath": "a.cs", "newLine": 5, "body": "Fund",
                 "severity": "High", "confidence": "Medium"},
            ],
        },
        "diagnostics": {"checkoutRequested": True, "warnings": [],
                        "durationSeconds": 12.5, "error": None},
    }


def test_summary_wird_als_kommentar_ohne_pfad_gefuehrt():
    entry = build_review_entry(record())
    assert entry["tool"] == "naudit"
    bodies = [c for c in entry["review_comments"] if c["path"] is None]
    assert len(bodies) == 1
    assert bodies[0]["body"] == "Zusammenfassung"
    assert bodies[0]["line"] is None


def test_inline_kommentare_behalten_pfad_und_zeile():
    entry = build_review_entry(record())
    inline = [c for c in entry["review_comments"] if c["path"] is not None]
    assert inline == [{"path": "a.cs", "line": 5, "body": "Fund", "created_at": None}]


def test_merge_laesst_golden_comments_und_fremde_tools_unberuehrt():
    data = {
        "https://github.com/getsentry/sentry/pull/1": {
            "golden_comments": [{"comment": "echter Mangel", "severity": "High"}],
            "reviews": [{"tool": "coderabbit", "review_comments": []}],
        }
    }
    merged = merge(data, [record()], force=False)
    pr = merged["https://github.com/getsentry/sentry/pull/1"]
    assert pr["golden_comments"] == [{"comment": "echter Mangel", "severity": "High"}]
    assert [r["tool"] for r in pr["reviews"]] == ["coderabbit", "naudit"]


def test_merge_verweigert_doppelten_import_ohne_force():
    data = {
        "https://github.com/getsentry/sentry/pull/1": {
            "golden_comments": [],
            "reviews": [{"tool": "naudit", "review_comments": []}],
        }
    }
    with pytest.raises(SystemExit):
        merge(data, [record()], force=False)


@pytest.mark.parametrize("diagnostics", [
    {"checkoutRequested": True, "warnings": [], "error": "Checkout fehlgeschlagen"},
    {"checkoutRequested": False, "warnings": [], "error": None},
    {"checkoutRequested": True, "warnings": ["Warning: git fetch schlug fehl"], "error": None},
])
def test_merge_verweigert_import_bei_degradiertem_review(diagnostics):
    # Alle drei Fälle heißen: das Review lief nicht unter vollen Bedingungen. Importiert
    # zählte es als "nichts gefunden" und würde den Recall verfälschen.
    bad = record()
    bad["diagnostics"] = diagnostics
    data = {"https://github.com/getsentry/sentry/pull/1": {"golden_comments": [], "reviews": []}}
    with pytest.raises(SystemExit):
        merge(data, [bad], force=False)


def test_merge_meldet_unbekannte_url():
    data = {}
    with pytest.raises(SystemExit):
        merge(data, [record("https://github.com/unbekannt/repo/pull/9")], force=False)
```

- [ ] **Step 2: Test laufen lassen und Fehlschlag bestätigen**

Run: `cd tools/benchmark && python -m pytest test_import_reviews.py -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'import_reviews'`

- [ ] **Step 3: Implementieren**

`tools/benchmark/import_reviews.py`:

```python
#!/usr/bin/env python3
"""Trägt Naudits aufgefangene Reviews als Tool `naudit` in die benchmark_data.json ein.

Bewusst konservativ: golden_comments und fremde Tool-Einträge werden gelesen und
unverändert zurückgeschrieben. Ein Review mit Fehlerdiagnose wird nicht importiert —
sonst zählte ein fehlgeschlagener Lauf als "nichts gefunden".
"""

import argparse
import json
import sys


def build_review_entry(record: dict) -> dict:
    """Baut den Review-Eintrag im Schema, das step1_download_prs.py erzeugt."""
    review = record["review"]
    comments = [
        # Summary: wie ein Top-Level-Review-Body — ohne Pfad und Zeile.
        {"path": None, "line": None, "body": review["summary"], "created_at": None}
    ]
    comments += [
        {"path": c["filePath"], "line": c["newLine"], "body": c["body"], "created_at": None}
        for c in review["comments"]
    ]
    return {
        "tool": "naudit",
        "repo_name": review["projectId"],
        "pr_url": record["url"],
        "review_comments": comments,
    }


def merge(data: dict, records: list[dict], force: bool) -> dict:
    for record in records:
        diag = record.get("diagnostics") or {}
        reason = None
        if diag.get("error"):
            reason = f"Fehler: {diag['error']}"
        elif not diag.get("checkoutRequested", False):
            reason = "kein Checkout angefragt — Review lief ohne Repo-Kontext"
        elif diag.get("warnings"):
            reason = "Warnungen der Pipeline: " + " | ".join(diag["warnings"])
        if reason:
            # Naudit ist fail-open: ein degradiertes Review sieht im Ergebnis nur schwächer aus.
            # Importiert verfälschte es den Recall — also wiederholen statt übernehmen.
            sys.exit(f"Abbruch: {record['url']} lief nicht unter vollen Bedingungen ({reason}).")

        url = record["url"]
        if url not in data:
            sys.exit(f"Abbruch: {url} kommt in benchmark_data.json nicht vor.")

        reviews = data[url].setdefault("reviews", [])
        if any(r.get("tool") == "naudit" for r in reviews):
            if not force:
                sys.exit(f"Abbruch: für {url} existiert bereits ein naudit-Eintrag (--force zum Ersetzen).")
            reviews[:] = [r for r in reviews if r.get("tool") != "naudit"]

        reviews.append(build_review_entry(record))
    return data


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reviews", required=True, help="naudit-reviews.json aus dem Runner")
    parser.add_argument("--benchmark-data", required=True, help="results/benchmark_data.json")
    parser.add_argument("--force", action="store_true", help="vorhandene naudit-Einträge ersetzen")
    args = parser.parse_args()

    with open(args.reviews, encoding="utf-8") as f:
        records = json.load(f)
    with open(args.benchmark_data, encoding="utf-8") as f:
        data = json.load(f)

    before = sum(len(e.get("reviews", [])) for e in data.values())
    merged = merge(data, records, args.force)
    after = sum(len(e.get("reviews", [])) for e in merged.values())

    with open(args.benchmark_data, "w", encoding="utf-8") as f:
        json.dump(merged, f, indent=2)

    print(f"{len(records)} Reviews importiert. Review-Einträge gesamt: {before} → {after}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 4: Test laufen lassen und Erfolg bestätigen**

Run: `cd tools/benchmark && python -m pytest test_import_reviews.py -v`
Expected: PASS (6 Tests)

- [ ] **Step 5: Committen**

```bash
git add tools/benchmark/import_reviews.py tools/benchmark/test_import_reviews.py
git commit -m "feat(benchmark): Import der aufgefangenen Reviews in benchmark_data.json"
```

---

### Task 7: Judge über OpenRouter erreichbar machen

`MARTIAN_MODEL` bestimmt **beides**: das Ausgabeverzeichnis und den Modellnamen im Aufruf.
OpenRouter führt die beiden Claude-Judges unter anderen Ids. Eine optionale zweite Variable trennt
die Rollen, damit Naudits Zahlen in den vorhandenen Verzeichnissen landen.

**Files:**
- Create: `tools/benchmark/judge-endpoint-mapping.patch`
- Create: `tools/benchmark/env.example`

- [ ] **Step 1: Patch schreiben**

`tools/benchmark/judge-endpoint-mapping.patch` — dieselbe Änderung in drei Dateien. In
`offline/code_review_benchmark/step2_extract_comments.py`,
`step2_5_dedup_candidates.py` und `step3_judge_comments.py` jeweils die Zeile

```python
        self.model = os.environ.get("MARTIAN_MODEL", "openai/gpt-4o-mini")
```

ersetzen durch

```python
        # Verzeichnisname (MARTIAN_MODEL) und Endpunkt-Modellname können auseinanderfallen:
        # OpenRouter führt dieselben Modelle unter anderen Ids als der Router der Autoren.
        # get_model_dir() liest weiterhin MARTIAN_MODEL — die Ergebnisse landen also dort,
        # wo die übrigen Tools bereits bewertet sind.
        self.model = os.environ.get("MARTIAN_MODEL_ENDPOINT") or os.environ.get("MARTIAN_MODEL", "openai/gpt-4o-mini")
```

Den Patch mit `git -C $NAUDIT_BENCHMARK_REPO diff > judge-endpoint-mapping.patch` erzeugen,
nachdem die drei Stellen von Hand geändert wurden.

- [ ] **Step 2: Vorlage für die Umgebung schreiben**

`tools/benchmark/env.example`:

```bash
# In den Benchmark-Klon als offline/.env kopieren.
GH_TOKEN=<read-only>
GITHUB_TOKEN=<read-only>

MARTIAN_API_KEY=<OpenRouter-Key>
MARTIAN_BASE_URL=https://openrouter.ai/api/v1

# Judge-Lauf 1 — Sonnet 4.5
# MARTIAN_MODEL=anthropic/claude-sonnet-4-5-20250929
# MARTIAN_MODEL_ENDPOINT=anthropic/claude-sonnet-4.5

# Judge-Lauf 2 — Opus 4.5
# MARTIAN_MODEL=anthropic/claude-opus-4-5-20251101
# MARTIAN_MODEL_ENDPOINT=anthropic/claude-opus-4.5
```

- [ ] **Step 3: Patch gegen den Klon prüfen**

```bash
cd $NAUDIT_BENCHMARK_REPO && git apply --check ~/workspace/Naudit/tools/benchmark/judge-endpoint-mapping.patch
```

Expected: keine Ausgabe (Patch passt sauber).

- [ ] **Step 4: Committen**

```bash
git add tools/benchmark/judge-endpoint-mapping.patch tools/benchmark/env.example
git commit -m "feat(benchmark): Judge-Endpunkt von Verzeichnisnamen entkoppeln (OpenRouter)"
```

---

### Task 8: Smoke-Test über einen PR

Kein Code — ein Gate. Er klärt in einem Durchgang, ob Abfang, URL-Auflösung, OpenRouter-Zugang
und Verzeichniszuordnung stimmen, bevor Stunden Laufzeit und Geld hineingehen.

- [ ] **Step 1: Voraussetzungen setzen**

```bash
claude setup-token                      # einmalig, liefert CLAUDE_CODE_OAUTH_TOKEN
export CLAUDE_CODE_OAUTH_TOKEN=<token>
export NAUDIT_BENCHMARK_REPO=~/workspace/code-review-benchmark
export Naudit__Git__Platform=GitHub
export Naudit__GitHub__Token=<read-only-PAT>
export Naudit__GitHub__WebhookSecret=benchmark-unused
export Naudit__Ai__Provider=ClaudeCode
export Naudit__Ai__Model=opus
export Naudit__Db__ConnectionString="Data Source=$NAUDIT_BENCHMARK_REPO/offline/results/naudit-benchmark.db"
```

- [ ] **Step 2: Einen einzigen PR reviewen**

```bash
NAUDIT_BENCHMARK_LIMIT=1 dotnet run --project tools/Naudit.Benchmark
```

Expected: ein Review mit Inline-Kommentaren, Verdict und Laufzeit; kein Eintrag im
Abschlussbericht unter „auffällige Reviews".

- [ ] **Step 3: Prüfen, dass nichts gepostet wurde**

Den PR aus der Ausgabe im Browser öffnen und bestätigen, dass **kein** Kommentar von dir
erschienen ist. Dieser Blick ist die eigentliche Absicherung — alles andere ist Code, der
behauptet, nichts zu tun.

- [ ] **Step 4: Importieren und judgen**

```bash
cd $NAUDIT_BENCHMARK_REPO/offline
git apply ~/workspace/Naudit/tools/benchmark/judge-endpoint-mapping.patch
cp ~/workspace/Naudit/tools/benchmark/env.example .env    # Keys eintragen, Sonnet-Block aktivieren
uv sync

python ~/workspace/Naudit/tools/benchmark/import_reviews.py \
  --reviews results/naudit-reviews.json --benchmark-data results/benchmark_data.json

uv run python -m code_review_benchmark.step2_extract_comments --tool naudit
uv run python -m code_review_benchmark.step2_5_dedup_candidates --tool naudit
uv run python -m code_review_benchmark.step3_judge_comments --tool naudit \
  --dedup-groups results/anthropic_claude-sonnet-4-5-20250929/dedup_groups.json
```

Expected: `results/anthropic_claude-sonnet-4-5-20250929/evaluations.json` enthält einen
`naudit`-Eintrag für diesen PR mit Precision/Recall — **im vorhandenen Verzeichnis**, nicht in
einem neuen. Ist das Verzeichnis neu, greift die Zuordnung aus Task 7 nicht.

- [ ] **Step 5: Ergebnis bewerten und entscheiden**

Bevor die anderen 49 laufen: sind die Kandidaten aus Naudits Kommentaren plausibel extrahiert?
Hat der Judge sinnvoll geurteilt? Wenn hier etwas schiefliegt, ist es 49-mal billiger, es jetzt
zu merken.

---

### Task 9: Vollständiger Lauf und Auswertung

- [ ] **Step 1: Alle verbleibenden PRs reviewen**

```bash
dotnet run --project tools/Naudit.Benchmark
```

Läuft Stunden. Bei Abbruch oder Rate-Limit einfach erneut starten — erledigte PRs werden
übersprungen. Am Ende muss `Fertig: 50/50, offen: 0` stehen und der Abschlussbericht leer sein.

- [ ] **Step 2: Importieren**

```bash
python ~/workspace/Naudit/tools/benchmark/import_reviews.py \
  --reviews results/naudit-reviews.json --benchmark-data results/benchmark_data.json --force
```

- [ ] **Step 3: Judge-Lauf Sonnet 4.5**

```bash
# .env: MARTIAN_MODEL=anthropic/claude-sonnet-4-5-20250929
#       MARTIAN_MODEL_ENDPOINT=anthropic/claude-sonnet-4.5
uv run python -m code_review_benchmark.step2_extract_comments --tool naudit
uv run python -m code_review_benchmark.step2_5_dedup_candidates --tool naudit
uv run python -m code_review_benchmark.step3_judge_comments --tool naudit \
  --dedup-groups results/anthropic_claude-sonnet-4-5-20250929/dedup_groups.json
```

> **Wiederholungslauf:** Wurde schon einmal importiert und bewertet, brauchen **alle drei**
> Schritte zusätzlich `--force`. Jeder überspringt bereits vorhandene Ergebnisse: `step2` seine
> Kandidaten, `step2_5` seine Dedup-Gruppen je (PR, Tool), `step3` seine erledigten
> (PR, Tool)-Paare. Ohne `--force` würden frische Reviews gegen alte Kandidaten und alte Urteile
> gerechnet — die Zahl wäre still die des vorigen Laufs. Das gilt auch dann, wenn der Import
> selbst mit `--force` lief: `--force` am Importer ersetzt nur den Eintrag in
> `benchmark_data.json`, es räumt die Zwischenergebnisse der Auswertungsschritte nicht ab.
> `--force --tool naudit` löscht dabei nur die naudit-Ergebnisse, die der 41 anderen Tools
> bleiben stehen.

- [ ] **Step 4: Judge-Lauf Opus 4.5**

Dieselben drei Befehle mit `MARTIAN_MODEL=anthropic/claude-opus-4-5-20251101` und
`MARTIAN_MODEL_ENDPOINT=anthropic/claude-opus-4.5`, `--dedup-groups` entsprechend auf
`results/anthropic_claude-opus-4-5-20251101/dedup_groups.json` — bei einem Wiederholungslauf
mit demselben `--force`-Vorbehalt.

- [ ] **Step 5: Dashboard und Export**

```bash
uv run python analysis/benchmark_dashboard.py
uv run python -m code_review_benchmark.step4_export_by_tool --tool naudit
```

Expected: `analysis/benchmark_dashboard.html` zeigt naudit neben den 41 anderen Tools;
`results/naudit_reviews.xlsx` enthält je Fund das Judge-Urteil samt Begründung — der Anhang
für die Arbeit.

- [ ] **Step 6: Ergebnisse dokumentieren**

Zahlen als **Lauf 13** in `~/workspace/BenediktsMind/1. Projects/Bachelorarbeit/Bachelorarbeit – Testergebnisse.md`
eintragen (der Platzhalter-Eintrag steht bereits) und die Aufbau-Notiz
`2026-08-04 Offline-Benchmark – Versuchsaufbau (naudit vs. 41 Code-Review-Tools)` um einen
Ergebnis-Abschnitt ergänzen: Platzierung, Precision/Recall-Profil, Stabilität über beide Judges.
