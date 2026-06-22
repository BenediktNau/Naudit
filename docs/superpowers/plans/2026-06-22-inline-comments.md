# Inline-/Positions-Kommentare — Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Naudit kommentiert Befunde an den konkreten Diff-Zeilen (Inline, CodeRabbit-Stil) auf GitLab **und** GitHub, plus einen schlanken Summary-Kommentar mit Verdict, Anzahl und nicht-verortbaren Findings.

**Architecture:** `Naudit.Core` bleibt SDK-frei: ein neuer `DiffParser` baut aus den vorhandenen `CodeChange.Diff`-Unified-Diffs je Datei die Menge kommentierbarer Zeilen; `ReviewService` validiert jede LLM-Finding dagegen und teilt sie in verortete `InlineComment`s und nicht-verortbare Findings (→ Summary). Eine generische `IGitPlatform.PostReviewAsync`-Methode ersetzt `PostSummaryAsync`; GitLab mappt `InlineComment` auf Discussions (mit `diff_refs`-SHAs), GitHub auf einen einzigen Reviews-API-Call.

**Tech Stack:** .NET 10, xUnit, `Microsoft.Extensions.AI` (Abstractions in Core), `System.Net.Http.Json`, `System.Text.Json`.

## Global Constraints

- Solution-Datei ist `Naudit.slnx` (nicht `.sln`). Build: `dotnet build Naudit.slnx`. Test: `dotnet test Naudit.slnx`.
- `Naudit.Core` hängt **nur** an `Microsoft.Extensions.AI.Abstractions` — kein Provider-/Plattform-SDK. `DiffParser`, `InlineComment` und die Summary-Komposition sind reine .NET-Logik.
- Abhängigkeitsrichtung bleibt `Web → Infrastructure → Core`.
- Code-Kommentare auf **Deutsch** (bestehende Konvention).
- **TDD:** je Task rot → grün → genau **ein** Commit. Jeder Task endet mit grüner Suite.
- Verdict-Mapping bleibt **fail-closed** (nur explizit `approve`/`request_changes`, sonst `InvalidOperationException`).
- Zeilen-Scope: hinzugefügte (`+`) **und** Kontextzeilen (` `) sind kommentierbar; gelöschte (`-`) nicht.
- Jede Commit-Message endet mit:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`

---

### Task 1: `InlineComment`-Modell + `DiffParser` (Core, additiv)

**Files:**
- Create: `src/Naudit.Core/Models/InlineComment.cs`
- Create: `src/Naudit.Core/Review/DiffParser.cs`
- Test: `tests/Naudit.Tests/DiffParserTests.cs`

**Interfaces:**
- Consumes: `Naudit.Core.Models.CodeChange(string FilePath, string Diff)` (bestehend).
- Produces:
  - `record InlineComment(string FilePath, int NewLine, int? OldLine, string Body)` in `Naudit.Core.Models`.
  - `DiffParser.Parse(IReadOnlyList<CodeChange> changes) : IReadOnlyDictionary<string, IReadOnlyDictionary<int, int?>>` — je Datei eine Map `NewLine → OldLine?` (null = hinzugefügte Zeile).
  - `internal static (int OldStart, int NewStart) DiffParser.ParseHunkHeader(string header)` (von Task 2 wiederverwendet).

- [ ] **Step 1: Failing Test schreiben**

`tests/Naudit.Tests/DiffParserTests.cs`:
```csharp
using Naudit.Core.Models;
using Naudit.Core.Review;
using Xunit;

namespace Naudit.Tests;

public class DiffParserTests
{
    [Fact]
    public void Parse_addedLines_haveNullOldLine_andSequentialNewLines()
    {
        var changes = new[] { new CodeChange("src/Foo.cs", "@@ -0,0 +1,2 @@\n+line1\n+line2") };

        var map = DiffParser.Parse(changes)["src/Foo.cs"];

        Assert.Null(map[1]);
        Assert.Null(map[2]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void Parse_contextLines_carryOldAndNewLine()
    {
        // @@ -1,2 +1,3 @@ : ctx(old1/new1), +new(new2), ctx2(old2/new3)
        var changes = new[] { new CodeChange("src/Foo.cs", "@@ -1,2 +1,3 @@\n ctx\n+new\n ctx2") };

        var map = DiffParser.Parse(changes)["src/Foo.cs"];

        Assert.Equal(1, map[1]);   // Kontextzeile: old 1
        Assert.Null(map[2]);       // hinzugefügt
        Assert.Equal(2, map[3]);   // Kontextzeile: old 2
    }

    [Fact]
    public void Parse_deletedLines_areNotCommentable()
    {
        var changes = new[] { new CodeChange("src/Foo.cs", "@@ -1,2 +1,1 @@\n ctx\n-removed") };

        var map = DiffParser.Parse(changes)["src/Foo.cs"];

        Assert.Equal(1, map[1]);   // nur die Kontextzeile
        Assert.Single(map);
    }

    [Fact]
    public void Parse_multipleHunks_continueNumbering()
    {
        var changes = new[] { new CodeChange("src/Foo.cs", "@@ -1,1 +1,1 @@\n+a\n@@ -10,1 +10,1 @@\n+b") };

        var map = DiffParser.Parse(changes)["src/Foo.cs"];

        Assert.Null(map[1]);
        Assert.Null(map[10]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void Parse_multipleFiles_areKeyedByPath()
    {
        var changes = new[]
        {
            new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x"),
            new CodeChange("b.cs", "@@ -0,0 +1,1 @@\n+y"),
        };

        var result = DiffParser.Parse(changes);

        Assert.True(result.ContainsKey("a.cs"));
        Assert.True(result.ContainsKey("b.cs"));
    }
}
```

- [ ] **Step 2: Test laufen lassen — muss fehlschlagen**

Run: `dotnet test Naudit.slnx --filter DiffParserTests`
Expected: FAIL (Build-Fehler: `DiffParser`/`InlineComment` existieren nicht).

- [ ] **Step 3: `InlineComment` schreiben**

`src/Naudit.Core/Models/InlineComment.cs`:
```csharp
namespace Naudit.Core.Models;

/// <summary>Ein Review-Kommentar, der an eine konkrete Diff-Zeile gebunden ist.</summary>
/// <param name="OldLine">Zeilennummer in der alten Datei — nur bei Kontextzeilen gesetzt, bei hinzugefügten Zeilen null.</param>
public sealed record InlineComment(string FilePath, int NewLine, int? OldLine, string Body);
```

- [ ] **Step 4: `DiffParser` schreiben**

`src/Naudit.Core/Review/DiffParser.cs`:
```csharp
using Naudit.Core.Models;

namespace Naudit.Core.Review;

/// <summary>Parst Unified-Diffs zu den kommentierbaren Zeilen je Datei.</summary>
public static class DiffParser
{
    /// <summary>
    /// Liefert je Datei eine Map New-File-Zeilennummer -> alte Zeilennummer (null bei hinzugefügter Zeile).
    /// Nur hinzugefügte (+) und Kontext-Zeilen ( ) sind enthalten; gelöschte (-) Zeilen nicht.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<int, int?>> Parse(IReadOnlyList<CodeChange> changes)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<int, int?>>();
        foreach (var change in changes)
        {
            var map = new Dictionary<int, int?>();
            int oldLine = 0, newLine = 0;
            foreach (var raw in change.Diff.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    var (oldStart, newStart) = ParseHunkHeader(line);
                    oldLine = oldStart - 1;
                    newLine = newStart - 1;
                    continue;
                }
                // Datei-Header (+++/---) sind kein Inhalt.
                if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                    continue;
                if (line.Length == 0)
                    continue;
                switch (line[0])
                {
                    case '+':
                        newLine++;
                        map[newLine] = null;
                        break;
                    case '-':
                        oldLine++;
                        break;
                    case ' ':
                        oldLine++;
                        newLine++;
                        map[newLine] = oldLine;
                        break;
                    // '\' (No newline at end of file) u. Ä. ignorieren
                }
            }
            result[change.FilePath] = map;
        }
        return result;
    }

    /// <summary>Liest aus "@@ -oldStart[,n] +newStart[,n] @@" die beiden Startzeilen.</summary>
    internal static (int OldStart, int NewStart) ParseHunkHeader(string header)
    {
        var minus = header.IndexOf('-');
        var plus = header.IndexOf('+');
        return (ReadNumber(header, minus + 1), ReadNumber(header, plus + 1));
    }

    private static int ReadNumber(string s, int start)
    {
        int i = start, val = 0;
        while (i < s.Length && char.IsDigit(s[i]))
        {
            val = val * 10 + (s[i] - '0');
            i++;
        }
        return val;
    }
}
```

- [ ] **Step 5: Test laufen lassen — muss bestehen**

Run: `dotnet test Naudit.slnx --filter DiffParserTests`
Expected: PASS (5 Tests).

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Core/Models/InlineComment.cs src/Naudit.Core/Review/DiffParser.cs tests/Naudit.Tests/DiffParserTests.cs
git commit -m "feat(core): InlineComment-Modell + DiffParser für kommentierbare Zeilen

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: `PromptBuilder` — Zeilennummern annotieren + Antwortschema erweitern (Core)

**Files:**
- Modify: `src/Naudit.Core/Review/PromtBuilder.cs` (Dateiname hat den bekannten "Promt"-Typo; Klasse heißt `PromptBuilder`)
- Test: `tests/Naudit.Tests/PromtBuilderTests.cs`

**Interfaces:**
- Consumes: `DiffParser.ParseHunkHeader` (Task 1).
- Produces: `PromptBuilder.Build` (Signatur unverändert) rendert jede Diff-Zeile mit vorangestellter New-File-Zeilennummer; `PromptBuilder.DefaultSystemPrompt` beschreibt das 3-Felder-Schema (`verdict`, `summary`, `comments`).

- [ ] **Step 1: Failing Test schreiben (Annotation prüfen)**

In `tests/Naudit.Tests/PromtBuilderTests.cs` einen Test ergänzen:
```csharp
    [Fact]
    public void Build_annotatesDiffLines_withNewFileLineNumbers()
    {
        var request = new ReviewRequest("1", 42, "Add feature X");
        var changes = new[] { new CodeChange("src/Bar.cs", "@@ -2,1 +2,2 @@\n ctx\n+added") };

        var messages = PromptBuilder.Build("SYS", request, changes);

        // Kontextzeile ist New-File-Zeile 2, hinzugefügte Zeile ist New-File-Zeile 3.
        Assert.Contains("2  ctx", messages[1].Text);
        Assert.Contains("3 +added", messages[1].Text);
    }
```

- [ ] **Step 2: Test laufen lassen — muss fehlschlagen**

Run: `dotnet test Naudit.slnx --filter PromptBuilderTests`
Expected: FAIL (Annotation fehlt; `messages[1].Text` enthält die Zeilennummern noch nicht).

- [ ] **Step 3: `PromptBuilder` implementieren**

`src/Naudit.Core/Review/PromtBuilder.cs` vollständig ersetzen:
```csharp
using System.Text;
using Microsoft.Extensions.AI;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

public static class PromptBuilder
{
    public const string DefaultSystemPrompt =
        "You are Naudit, a senior code reviewer. Review the merge request diff below. " +
        "Each diff line is prefixed with its line number in the NEW file (blank for removed lines). " +
        "Focus on correctness bugs, security issues and clear maintainability problems. Be concise. " +
        "Respond ONLY with a JSON object with exactly three fields: " +
        "\"verdict\" - either \"approve\" or \"request_changes\" " +
        "(use \"request_changes\" only when there are correctness or security bugs that should block the merge); " +
        "\"summary\" - GitHub-flavored Markdown: a one-line overview (if there are no significant issues, say so briefly); " +
        "\"comments\" - an array of findings tied to a line, each " +
        "{ \"file\": <path exactly as shown>, \"line\": <new-file line number shown in the diff>, \"comment\": <Markdown> }. " +
        "Only use a line number that is shown at the start of a line in the diff. " +
        "If a finding does not map to one specific changed line, omit it from \"comments\" and mention it in \"summary\" instead.";

    public static IList<ChatMessage> Build(string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Merge Request: {request.Title}");
        foreach (var change in changes)
        {
            sb.AppendLine();
            sb.AppendLine($"## File: {change.FilePath}");
            sb.AppendLine("```diff");
            AppendAnnotatedDiff(sb, change.Diff);
            sb.AppendLine("```");
        }

        return new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, sb.ToString()),
        };
    }

    // Stellt jeder Diff-Zeile ihre New-File-Zeilennummer voran (leer bei gelöschten/Header-Zeilen),
    // damit das LLM eine stabile, reale Zeilennummer referenzieren kann.
    private static void AppendAnnotatedDiff(StringBuilder sb, string diff)
    {
        int newLine = 0;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                newLine = DiffParser.ParseHunkHeader(line).NewStart - 1;
                sb.AppendLine($"     {line}");
                continue;
            }
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                sb.AppendLine($"     {line}");
                continue;
            }
            if (line.Length > 0 && (line[0] == '+' || line[0] == ' '))
            {
                newLine++;
                sb.AppendLine($"{newLine,4} {line}");
            }
            else
            {
                // gelöschte Zeile / sonstiges: keine New-File-Nummer
                sb.AppendLine($"     {line}");
            }
        }
    }
}
```

> **Hinweis zur Annotation:** `{newLine,4}` rechtsbündig auf 4 Stellen, dann ein Leerzeichen, dann die Originalzeile. Für `" ctx"` (Kontext, New-Line 2) ergibt das `"   2  ctx"` → enthält Teilstring `"2  ctx"` (zwei Leerzeichen: Trenn-Space + das diff-eigene Kontext-Space). Für `"+added"` (New-Line 3) ergibt das `"   3 +added"` → enthält `"3 +added"`.

- [ ] **Step 4: Tests laufen lassen — müssen bestehen**

Run: `dotnet test Naudit.slnx --filter PromptBuilderTests`
Expected: PASS (beide Tests — der bestehende `Build_putsSystemPromptFirst_andEmbedsDiffsAndPaths` und der neue).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Core/Review/PromtBuilder.cs tests/Naudit.Tests/PromtBuilderTests.cs
git commit -m "feat(core): Diff-Zeilen annotieren + comments[] ins Antwortschema

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: `IGitPlatform.PostReviewAsync` — Interface umbenennen + Fakes (kein Verhaltenswechsel)

Dieser Task benennt das Interface um und hält **alle** Implementierungen/Aufrufer/Tests grün — **ohne** neues Inline-Verhalten. GitLab postet weiter nur die Note, GitHub weiter den Issue-Kommentar; `ReviewService` übergibt eine leere Kommentarliste. Inline-Posting folgt in Task 4 (GitLab) und Task 5 (GitHub), die Befund-Erzeugung in Task 6.

**Files:**
- Modify: `src/Naudit.Core/Abstractions/IGitPlatform.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs:22`
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs:25`
- Modify: `src/Naudit.Core/Review/ReviewService.cs:37`
- Modify: `tests/Naudit.Tests/Fakes/FakeGitPlatform.cs`
- Modify: `tests/Naudit.Tests/GitLabPlatformTests.cs:37-46`
- Modify: `tests/Naudit.Tests/GitHubPlatformTests.cs:57-67`

**Interfaces:**
- Produces: `Task IGitPlatform.PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, CancellationToken ct = default)`.
- Produces: `FakeGitPlatform.PostedMarkdown`, `FakeGitPlatform.PostedComments` (`IReadOnlyList<InlineComment>`), `FakeGitPlatform.PostCallCount`.

- [ ] **Step 1: Interface ändern**

`src/Naudit.Core/Abstractions/IGitPlatform.cs` — `PostSummaryAsync` ersetzen:
```csharp
using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Git-Plattform-Adapter. GitLab und GitHub als Implementierungen vorhanden (per Config gewählt).</summary>
public interface IGitPlatform
{
    Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default);

    /// <summary>Postet den Summary-Kommentar und alle Inline-Kommentare an ihre Diff-Positionen.</summary>
    Task PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, CancellationToken ct = default);
}
```

- [ ] **Step 2: `GitLabPlatform` anpassen (nur Umbenennung)**

`src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs` — `PostSummaryAsync` ersetzen:
```csharp
    public async Task PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, CancellationToken ct = default)
    {
        var url = $"api/v4/projects/{request.ProjectId}/merge_requests/{request.MergeRequestIid}/notes";
        var response = await http.PostAsJsonAsync(url, new { body = summaryMarkdown }, ct);
        response.EnsureSuccessStatusCode();
        // Inline-Kommentare folgen in Task 4.
    }
```
Import ergänzen, falls nötig: `using Naudit.Core.Models;` (für `InlineComment`) ist über `Naudit.Core.Models` bereits vorhanden — prüfen.

- [ ] **Step 3: `GitHubPlatform` anpassen (nur Umbenennung)**

`src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs` — `PostSummaryAsync` ersetzen:
```csharp
    public async Task PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, CancellationToken ct = default)
    {
        // PR-Kommentar = Issue-Kommentar (gleiche Nummer). Inline-Kommentare folgen in Task 5.
        var url = $"repos/{request.ProjectId}/issues/{request.MergeRequestIid}/comments";
        var response = await http.PostAsJsonAsync(url, new { body = summaryMarkdown }, ct);
        response.EnsureSuccessStatusCode();
    }
```

- [ ] **Step 4: `ReviewService`-Aufrufstelle anpassen**

`src/Naudit.Core/Review/ReviewService.cs:37` ändern:
```csharp
        await gitPlatform.PostReviewAsync(request, parsed.Summary, [], ct);
```

- [ ] **Step 5: `FakeGitPlatform` anpassen**

`tests/Naudit.Tests/Fakes/FakeGitPlatform.cs` vollständig ersetzen:
```csharp
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

internal sealed class FakeGitPlatform(IReadOnlyList<CodeChange> changes) : IGitPlatform
{
    public string? PostedMarkdown { get; private set; }
    public IReadOnlyList<InlineComment> PostedComments { get; private set; } = [];
    public int PostCallCount { get; private set; }

    public Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult(changes);

    public Task PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, CancellationToken ct = default)
    {
        PostedMarkdown = summaryMarkdown;
        PostedComments = comments;
        PostCallCount++;
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 6: GitLab-/GitHub-Platform-Tests anpassen (nur Umbenennung des Aufrufs)**

`tests/Naudit.Tests/GitLabPlatformTests.cs` — Test `PostSummaryAsync_postsNoteWithBody` umbenennen/anpassen:
```csharp
    [Fact]
    public async Task PostReviewAsync_postsNoteWithBody()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.Created, "", capture));

        await platform.PostReviewAsync(Request, "## Naudit Review", []);

        Assert.Equal(HttpMethod.Post, capture.LastRequest!.Method);
        Assert.Contains("/merge_requests/42/notes", capture.LastRequest.RequestUri!.ToString());
        Assert.Contains("Naudit Review", capture.LastRequestBody!);
    }
```

`tests/Naudit.Tests/GitHubPlatformTests.cs` — Test `PostSummaryAsync_postsIssueCommentWithBody` umbenennen/anpassen:
```csharp
    [Fact]
    public async Task PostReviewAsync_postsIssueCommentWithBody()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.Created, "", capture));

        await platform.PostReviewAsync(Request, "## Naudit Review", []);

        Assert.Equal(HttpMethod.Post, capture.LastRequest!.Method);
        Assert.Contains("repos/octo/hello-world/issues/42/comments", capture.LastRequest.RequestUri!.ToString());
        Assert.Contains("Naudit Review", capture.LastRequestBody!);
    }
```

- [ ] **Step 7: Build + volle Suite — muss grün sein**

Run: `dotnet test Naudit.slnx`
Expected: PASS (alle bestehenden Tests; nur Methodennamen geändert, kein Verhaltenswechsel).

- [ ] **Step 8: Commit**

```bash
git add src/Naudit.Core/Abstractions/IGitPlatform.cs src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs src/Naudit.Core/Review/ReviewService.cs tests/Naudit.Tests/Fakes/FakeGitPlatform.cs tests/Naudit.Tests/GitLabPlatformTests.cs tests/Naudit.Tests/GitHubPlatformTests.cs
git commit -m "refactor: IGitPlatform.PostSummaryAsync -> PostReviewAsync(summary, comments)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: GitLab — Inline-Kommentare als Discussions posten

**Files:**
- Modify: `tests/Naudit.Tests/Fakes/StubHttpMessageHandler.cs` (alle Calls aufzeichnen)
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabDtos.cs` (DTO für `diff_refs`)
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs`
- Test: `tests/Naudit.Tests/GitLabPlatformTests.cs`

**Interfaces:**
- Consumes: `InlineComment` (Task 1), `IGitPlatform.PostReviewAsync` (Task 3).
- Produces: `StubHttpMessageHandler.Calls` (`List<(HttpMethod Method, Uri? Uri, string? Body)>`); GitLab postet je `InlineComment` eine Discussion mit `position`.

- [ ] **Step 1: `StubHttpMessageHandler` erweitern (alle Calls aufzeichnen)**

`tests/Naudit.Tests/Fakes/StubHttpMessageHandler.cs` vollständig ersetzen:
```csharp
namespace Naudit.Tests.Fakes;

internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    // Alle Requests in Reihenfolge — nötig, weil PostReviewAsync mehrere Calls absetzt (GitLab: GET + N×POST).
    public List<(HttpMethod Method, Uri? Uri, string? Body)> Calls { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        string? body = null;
        if (request.Content is not null)
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        LastRequestBody = body;
        Calls.Add((request.Method, request.RequestUri, body));
        return responder(request);
    }
}
```

- [ ] **Step 2: Failing Test schreiben**

In `tests/Naudit.Tests/GitLabPlatformTests.cs` ergänzen:
```csharp
    [Fact]
    public async Task PostReviewAsync_postsDiscussionWithPosition_perInlineComment()
    {
        var capture = new StubHttpMessageHandler(req =>
        {
            // GET der MR-Details liefert die diff_refs; alle POSTs sind 201.
            if (req.Method == HttpMethod.Get && req.RequestUri!.ToString().EndsWith("/merge_requests/42"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{ "diff_refs": { "base_sha": "b1", "head_sha": "h1", "start_sha": "s1" } }""",
                        Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.Created, "", capture));

        var comments = new[]
        {
            new InlineComment("src/Foo.cs", 5, null, "added-line finding"),
            new InlineComment("src/Bar.cs", 7, 3, "context finding"),
        };

        await platform.PostReviewAsync(Request, "## Naudit Review", comments);

        var discussions = capture.Calls
            .Where(c => c.Method == HttpMethod.Post && c.Uri!.ToString().Contains("/discussions"))
            .ToList();
        Assert.Equal(2, discussions.Count);
        // diff_refs in der Position
        Assert.All(discussions, d => Assert.Contains("\"head_sha\":\"h1\"", d.Body!));
        // hinzugefügte Zeile: new_line ohne old_line
        Assert.Contains(discussions, d => d.Body!.Contains("\"new_line\":5") && !d.Body.Contains("old_line"));
        // Kontextzeile: new_line UND old_line
        Assert.Contains(discussions, d => d.Body!.Contains("\"new_line\":7") && d.Body.Contains("\"old_line\":3"));
        // Summary-Note wurde ebenfalls gepostet
        Assert.Contains(capture.Calls, c => c.Uri!.ToString().Contains("/notes"));
    }
```

- [ ] **Step 3: Test laufen lassen — muss fehlschlagen**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~PostReviewAsync_postsDiscussionWithPosition"`
Expected: FAIL (es wird keine Discussion gepostet; `discussions.Count` = 0).

- [ ] **Step 4: DTO für `diff_refs` ergänzen**

In `src/Naudit.Infrastructure/Git/GitLab/GitLabDtos.cs` ans Ende ergänzen:
```csharp
public sealed class GitLabMergeRequestDetail
{
    [JsonPropertyName("diff_refs")] public GitLabDiffRefs? DiffRefs { get; set; }
}

public sealed class GitLabDiffRefs
{
    [JsonPropertyName("base_sha")] public string BaseSha { get; set; } = "";
    [JsonPropertyName("head_sha")] public string HeadSha { get; set; } = "";
    [JsonPropertyName("start_sha")] public string StartSha { get; set; } = "";
}
```

- [ ] **Step 5: `GitLabPlatform.PostReviewAsync` implementieren**

`src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs` — `PostReviewAsync` ersetzen:
```csharp
    public async Task PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, CancellationToken ct = default)
    {
        var basePath = $"api/v4/projects/{request.ProjectId}/merge_requests/{request.MergeRequestIid}";

        // 1) Summary als normale Note.
        (await http.PostAsJsonAsync($"{basePath}/notes", new { body = summaryMarkdown }, ct)).EnsureSuccessStatusCode();

        if (comments.Count == 0)
            return;

        // 2) diff_refs (base/head/start SHA) für die Discussion-Position holen.
        var detail = await http.GetFromJsonAsync<GitLabMergeRequestDetail>(basePath, ct);
        var refs = detail?.DiffRefs
            ?? throw new InvalidOperationException("GitLab lieferte keine diff_refs für die Inline-Position.");

        // 3) Je Inline-Kommentar eine Discussion mit text-Position posten.
        foreach (var c in comments)
        {
            var position = new Dictionary<string, object?>
            {
                ["position_type"] = "text",
                ["base_sha"] = refs.BaseSha,
                ["head_sha"] = refs.HeadSha,
                ["start_sha"] = refs.StartSha,
                ["new_path"] = c.FilePath,
                ["new_line"] = c.NewLine,
            };
            // Kontextzeile: GitLab braucht zusätzlich die alte Position.
            if (c.OldLine is int oldLine)
            {
                position["old_path"] = c.FilePath;
                position["old_line"] = oldLine;
            }

            var payload = new { body = c.Body, position };
            (await http.PostAsJsonAsync($"{basePath}/discussions", payload, ct)).EnsureSuccessStatusCode();
        }
    }
```

- [ ] **Step 6: Tests laufen lassen — müssen bestehen**

Run: `dotnet test Naudit.slnx --filter GitLabPlatformTests`
Expected: PASS (inkl. neuem Discussion-Test und unverändertem Note-Test).

- [ ] **Step 7: Commit**

```bash
git add tests/Naudit.Tests/Fakes/StubHttpMessageHandler.cs src/Naudit.Infrastructure/Git/GitLab/GitLabDtos.cs src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs tests/Naudit.Tests/GitLabPlatformTests.cs
git commit -m "feat(gitlab): Inline-Kommentare als Discussions mit diff_refs-Position

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: GitHub — Inline-Kommentare über die Reviews-API (ein Call)

**Files:**
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs`
- Test: `tests/Naudit.Tests/GitHubPlatformTests.cs`

**Interfaces:**
- Consumes: `InlineComment` (Task 1), `IGitPlatform.PostReviewAsync` (Task 3).
- Produces: GitHub postet `body` + `comments[]` in **einem** `POST .../pulls/{n}/reviews` (`event: "COMMENT"`, `side: "RIGHT"`).

- [ ] **Step 1: Failing Tests schreiben (Summary-only + Inline)**

In `tests/Naudit.Tests/GitHubPlatformTests.cs` den in Task 3 angepassten `PostReviewAsync_postsIssueCommentWithBody`-Test durch diese zwei ersetzen:
```csharp
    [Fact]
    public async Task PostReviewAsync_withoutComments_postsReviewBody()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.Created, "", capture));

        await platform.PostReviewAsync(Request, "## Naudit Review", []);

        Assert.Equal(HttpMethod.Post, capture.LastRequest!.Method);
        Assert.Contains("repos/octo/hello-world/pulls/42/reviews", capture.LastRequest.RequestUri!.ToString());
        Assert.Contains("Naudit Review", capture.LastRequestBody!);
        Assert.Contains("\"event\":\"COMMENT\"", capture.LastRequestBody!);
    }

    [Fact]
    public async Task PostReviewAsync_withComments_includesPathLineSide()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.Created, "", capture));

        await platform.PostReviewAsync(Request, "## Naudit Review",
            [new InlineComment("src/Foo.cs", 5, null, "finding here")]);

        var body = capture.LastRequestBody!;
        Assert.Contains("repos/octo/hello-world/pulls/42/reviews", capture.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"path\":\"src/Foo.cs\"", body);
        Assert.Contains("\"line\":5", body);
        Assert.Contains("\"side\":\"RIGHT\"", body);
        Assert.Contains("finding here", body);
    }
```

- [ ] **Step 2: Tests laufen lassen — müssen fehlschlagen**

Run: `dotnet test Naudit.slnx --filter GitHubPlatformTests`
Expected: FAIL (postet noch an `issues/comments`, nicht an `pulls/.../reviews`).

- [ ] **Step 3: `GitHubPlatform.PostReviewAsync` implementieren**

`src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs` — `PostReviewAsync` ersetzen:
```csharp
    public async Task PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, CancellationToken ct = default)
    {
        // Ein Review-Call trägt Summary (body) UND alle Inline-Kommentare. event=COMMENT:
        // Naudit gatet nicht über GitHubs eigenen Review-Status (Verdict läuft über ReviewResult).
        var url = $"repos/{request.ProjectId}/pulls/{request.MergeRequestIid}/reviews";
        var payload = new
        {
            body = summaryMarkdown,
            @event = "COMMENT",
            comments = comments.Select(c => new
            {
                path = c.FilePath,
                line = c.NewLine,
                side = "RIGHT",
                body = c.Body,
            }).ToArray(),
        };
        var response = await http.PostAsJsonAsync(url, payload, ct);
        response.EnsureSuccessStatusCode();
    }
```

- [ ] **Step 4: Tests laufen lassen — müssen bestehen**

Run: `dotnet test Naudit.slnx --filter GitHubPlatformTests`
Expected: PASS (beide neuen Tests + `GetChangesAsync`-Tests).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs tests/Naudit.Tests/GitHubPlatformTests.cs
git commit -m "feat(github): Inline-Kommentare über die Reviews-API (body + comments, ein Call)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 6: `ReviewService` — Findings validieren, aufteilen, Summary komponieren

**Files:**
- Modify: `src/Naudit.Core/Review/ReviewService.cs`
- Modify: `tests/Naudit.Tests/ReviewServiceTests.cs`
- Modify: `CLAUDE.md` (Doku-Verweise auf `PostSummaryAsync` / „single summary comment")

**Interfaces:**
- Consumes: `DiffParser.Parse` (Task 1), `InlineComment` (Task 1), `IGitPlatform.PostReviewAsync` (Task 3), `FakeGitPlatform.PostedComments` (Task 3).
- Produces: `ReviewService.ReviewAsync` postet validierte `InlineComment`s inline und einen komponierten Summary (LLM-Summary + Verdict-Zeile + Count + „Findings ohne Position").

- [ ] **Step 1: Failing Tests schreiben**

In `tests/Naudit.Tests/ReviewServiceTests.cs`:
1. Die bestehenden Exact-Equality-Asserts auf `git.PostedMarkdown` auf `Assert.Contains` umstellen (die Summary ist jetzt komponiert):

```csharp
    [Fact]
    public async Task ReviewAsync_postsComposedSummary_andReturnsApprove()
    {
        var chat = new FakeChatClient("""{"summary":"## Review\n- looks fine","verdict":"approve","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        var result = await service.ReviewAsync(Request);

        Assert.Contains("looks fine", git.PostedMarkdown!);
        Assert.Contains("approve", git.PostedMarkdown!);
        Assert.Empty(git.PostedComments);
        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
        Assert.Equal("SYS", chat.LastMessages![0].Text);
    }

    [Fact]
    public async Task ReviewAsync_returnsRequestChanges_whenModelSaysSo()
    {
        var chat = new FakeChatClient("""{"summary":"## Review\n- bug here","verdict":"request_changes","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.RequestChanges, result.Verdict);
        Assert.Contains("bug here", git.PostedMarkdown!);
    }
```

2. Zwei neue Tests für Inline-Validierung ergänzen:
```csharp
    [Fact]
    public async Task ReviewAsync_validLine_isPostedInline()
    {
        var chat = new FakeChatClient(
            """{"summary":"## Review","verdict":"request_changes","comments":[{"file":"src/Foo.cs","line":1,"comment":"null deref"}]}""");
        var git = new FakeGitPlatform([new CodeChange("src/Foo.cs", "@@ -0,0 +1,1 @@\n+var x = foo();")]);
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await service.ReviewAsync(Request);

        var inline = Assert.Single(git.PostedComments);
        Assert.Equal("src/Foo.cs", inline.FilePath);
        Assert.Equal(1, inline.NewLine);
        Assert.Equal("null deref", inline.Body);
    }

    [Fact]
    public async Task ReviewAsync_invalidLine_goesToSummary_notInline()
    {
        var chat = new FakeChatClient(
            """{"summary":"## Review","verdict":"request_changes","comments":[{"file":"src/Foo.cs","line":99,"comment":"orphan finding"}]}""");
        var git = new FakeGitPlatform([new CodeChange("src/Foo.cs", "@@ -0,0 +1,1 @@\n+only one line")]);
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await service.ReviewAsync(Request);

        Assert.Empty(git.PostedComments);
        Assert.Contains("orphan finding", git.PostedMarkdown!);
        Assert.Contains("ohne Position", git.PostedMarkdown!);
    }
```

Die bestehenden Tests `ReviewAsync_withNoChanges_postsNothing_andApproves` und `ReviewAsync_withUnknownVerdict_throws` bleiben unverändert. (Der bisherige `ReviewAsync_postsSummary_andReturnsApprove` wird durch `ReviewAsync_postsComposedSummary_andReturnsApprove` ersetzt.)

- [ ] **Step 2: Tests laufen lassen — müssen fehlschlagen**

Run: `dotnet test Naudit.slnx --filter ReviewServiceTests`
Expected: FAIL (Inline-Comments werden noch nicht erzeugt; `PostedComments` leer, Summary nicht komponiert).

- [ ] **Step 3: `ReviewService` implementieren**

`src/Naudit.Core/Review/ReviewService.cs` vollständig ersetzen:
```csharp
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

public sealed class ReviewService(IChatClient chatClient, IGitPlatform gitPlatform, ReviewOptions options)
{
    // Web-Defaults: camelCase + case-insensitive — passt zu summary/verdict/comments.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var changes = await gitPlatform.GetChangesAsync(request, ct);
        if (changes.Count == 0)
            return new ReviewResult(string.Empty, ReviewVerdict.Approve);

        var messages = PromptBuilder.Build(options.SystemPrompt, request, changes);

        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        var response = await chatClient.GetResponseAsync(messages, chatOptions, ct);

        var parsed = JsonSerializer.Deserialize<LlmReviewResponse>(response.Text, JsonOpts)
            ?? throw new InvalidOperationException("LLM lieferte keine parsebare Review-Antwort.");

        // Fail-closed: nur explizite Verdicts; alles andere ist ein Fehler.
        var verdict = parsed.Verdict?.ToLowerInvariant() switch
        {
            "request_changes" => ReviewVerdict.RequestChanges,
            "approve" => ReviewVerdict.Approve,
            _ => throw new InvalidOperationException($"Unerwartetes Verdict vom LLM: '{parsed.Verdict}'."),
        };

        // Jede Finding gegen die kommentierbaren Diff-Zeilen prüfen.
        var commentable = DiffParser.Parse(changes);
        var inline = new List<InlineComment>();
        var orphans = new List<LlmComment>();
        foreach (var c in parsed.Comments ?? [])
        {
            if (commentable.TryGetValue(c.File, out var lines) && lines.TryGetValue(c.Line, out var oldLine))
                inline.Add(new InlineComment(c.File, c.Line, oldLine, c.Comment));
            else
                orphans.Add(c);
        }

        var summary = ComposeSummary(parsed.Summary, verdict, inline.Count, orphans);
        await gitPlatform.PostReviewAsync(request, summary, inline, ct);
        return new ReviewResult(summary, verdict);
    }

    // Schlanker Hybrid: LLM-Überblick + Verdict-Zeile + Count + nicht-verortbare Findings.
    private static string ComposeSummary(string llmSummary, ReviewVerdict verdict, int inlineCount, IReadOnlyList<LlmComment> orphans)
    {
        var sb = new StringBuilder();
        sb.AppendLine(llmSummary.TrimEnd());
        sb.AppendLine();
        var verdictText = verdict == ReviewVerdict.RequestChanges ? "⚠️ request_changes" : "✅ approve";
        sb.AppendLine($"**Verdict:** {verdictText} · {inlineCount} inline, {orphans.Count} ohne Position");
        if (orphans.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Findings ohne Position:**");
            foreach (var o in orphans)
            {
                var where = string.IsNullOrEmpty(o.File) ? "" : $"`{o.File}` ";
                sb.AppendLine($"- {where}{o.Comment}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    // Wire-DTO für die LLM-Antwort. Verdict bewusst als string (Mapping oben).
    private sealed record LlmReviewResponse(string Summary, string Verdict, List<LlmComment>? Comments);

    private sealed record LlmComment(string File, int Line, string Comment);
}
```

- [ ] **Step 4: Tests laufen lassen — müssen bestehen**

Run: `dotnet test Naudit.slnx --filter ReviewServiceTests`
Expected: PASS (alle ReviewService-Tests).

- [ ] **Step 5: `CLAUDE.md`-Doku angleichen**

In `CLAUDE.md` diese Stellen anpassen:
1. Projektbeschreibung (oben): „posts a single summary Markdown comment back to the MR/PR" →
   „posts inline comments on the changed lines plus one summary comment back to the MR/PR".
2. Web-Abschnitt: „validates the secret/signature, maps the payload" bleibt; der Verweis auf den
   Summary-Post unverändert lassen, aber im **Request flow** `IGitPlatform.PostSummaryAsync` →
   `IGitPlatform.PostReviewAsync` ersetzen:
   `→ IChatClient.GetResponseAsync → IGitPlatform.PostReviewAsync`.

- [ ] **Step 6: Volle Suite + Build — muss grün sein**

Run: `dotnet test Naudit.slnx`
Expected: PASS (gesamte Suite).

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Core/Review/ReviewService.cs tests/Naudit.Tests/ReviewServiceTests.cs CLAUDE.md
git commit -m "feat(core): Findings gegen Diff validieren, Inline/Summary aufteilen

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review

**Spec-Abdeckung** (gegen `docs/superpowers/specs/2026-06-22-inline-comments-design.md`):
- `InlineComment(FilePath, NewLine, OldLine?, Body)` → Task 1 ✅
- `DiffParser` (added+context, deleted excluded, mehrere Hunks/Dateien) → Task 1 ✅
- LLM-Schema `{verdict, summary, comments[]}` + Prompt-Annotation → Task 2 ✅
- `ReviewService`-Ablauf (validieren, aufteilen, Summary komponieren, fail-closed) → Task 6 ✅
- Interface `PostReviewAsync(request, summary, comments)` → Task 3 ✅
- GitLab: `/notes` + N×`/discussions` mit `position` (diff_refs via GET, old_line nur bei Kontext) → Task 4 ✅
- GitHub: ein `/pulls/{n}/reviews`-Call (`body`+`comments[]`, `event:COMMENT`, `side:RIGHT`) → Task 5 ✅
- Tests: DiffParser, PromptBuilder-Annotation, ReviewService (inline/orphan), GitLab-Discussion, GitHub-Review → Tasks 1,2,4,5,6 ✅

**Platzhalter-Scan:** keine TBD/TODO ohne Code; jeder Code-Step enthält vollständigen Code. ✅

**Typ-Konsistenz:** `PostReviewAsync(ReviewRequest, string, IReadOnlyList<InlineComment>, CancellationToken)` identisch in Interface (T3), beiden Plattformen (T3/T4/T5) und `FakeGitPlatform` (T3). `DiffParser.Parse` Rückgabe `IReadOnlyDictionary<string, IReadOnlyDictionary<int, int?>>` konsistent zwischen T1 und Nutzung in T6. `LlmComment(File, Line, Comment)` nur in T6. ✅

## Verweise
- Spec: `docs/superpowers/specs/2026-06-22-inline-comments-design.md`
- Architektur: `CLAUDE.md`
- Board: `1. Projects/Naudit/Doings.md` → „Inline-/Positions-Kommentare"
