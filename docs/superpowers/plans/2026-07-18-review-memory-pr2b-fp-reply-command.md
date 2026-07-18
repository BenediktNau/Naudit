# Review Memory PR 2b — `@naudit fp` Reply Command Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a repo member mark a Naudit finding as a false positive by replying `@naudit fp <optional reason>` on the bot's inline comment — creating the same memory entry the WebUI FP button does, without leaving the MR/PR.

**Architecture:** Extend the two existing webhook endpoints to also accept comment events (GitHub `pull_request_review_comment` / GitLab Note Hook), validated by the same signature/token check. A platform-agnostic parser turns the comment body into a normalized `ReviewCommentReply`; a scoped `ReviewCommentCommandService` (Infrastructure) authorizes the author via an `IReviewCommentResponder` platform seam, resolves the reply to a `ReviewFindingEntity` via the `PlatformCommentId` captured in PR 2a, writes the false-positive entry through a shared `MemoryEntryWriter`, and posts a confirmation reply. **No change to Core** (the only Core signature change was PR 2a's `PostReviewAsync` return). **No migration** (`PlatformCommentId`/`PlatformNoteId` already exist).

**Tech Stack:** .NET 10, ASP.NET Minimal API, EF Core (SQLite/Postgres), `System.Text.Json`, xUnit + `StubHttpMessageHandler` + `WebApplicationFactory<Program>`.

## Global Constraints

- Solution file is `Naudit.slnx` (XML format). Build: `dotnet build Naudit.slnx`. Test a class: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter <Name>`.
- **Core rule:** `Naudit.Core` depends only on `Microsoft.Extensions.AI.Abstractions`. All new types in this plan live in `Naudit.Infrastructure` or `Naudit.Web` — **do not** add types to `Naudit.Core`.
- Code comments are in German (match the surrounding files). User-facing docs (`docs/`, README) are English.
- Per-request auth only: git-API tokens come from `IGitTokenProvider.ResolveTokenAsync(projectId, ct)` set on each `HttpRequestMessage`, never as a static default header.
- Fail-closed authorization: an unverifiable author ⇒ ignore (log, HTTP 200). Every webhook path returns **200 after a passing signature check**, whatever happens next.
- The bot's confirmation reply is exactly `"Als False Positive gemerkt."` (German, matching existing bot texts).
- TDD: red → green → commit per task. DRY, YAGNI.

---

### Task 1: FP reply-command parser

**Files:**
- Create: `src/Naudit.Infrastructure/Git/FpReplyCommand.cs`
- Test: `tests/Naudit.Tests/FpReplyCommandTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Naudit.Infrastructure.Git.FpReplyCommand.TryParse(string? body) -> ParsedFpCommand?` and `record ParsedFpCommand(string? Reason)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Naudit.Tests/FpReplyCommandTests.cs
using Naudit.Infrastructure.Git;
using Xunit;

namespace Naudit.Tests;

public class FpReplyCommandTests
{
    [Theory]
    [InlineData("@naudit fp")]
    [InlineData("  @naudit   fp  ")]
    [InlineData("@Naudit FP")]
    [InlineData("@naudit false-positive")]
    public void TryParse_recognisesCommand_withoutReason(string body)
    {
        var cmd = FpReplyCommand.TryParse(body);
        Assert.NotNull(cmd);
        Assert.Null(cmd!.Reason);
    }

    [Theory]
    [InlineData("@naudit fp this is intentional", "this is intentional")]
    [InlineData("@naudit false-positive because legacy", "because legacy")]
    [InlineData("@NAUDIT FP  trailing spaces  ", "trailing spaces")]
    public void TryParse_extractsReason_restOfLine(string body, string expected)
    {
        var cmd = FpReplyCommand.TryParse(body);
        Assert.NotNull(cmd);
        Assert.Equal(expected, cmd!.Reason);
    }

    [Fact]
    public void TryParse_readsOnlyFirstLine_forReason()
    {
        var cmd = FpReplyCommand.TryParse("@naudit fp only this\nnot this");
        Assert.Equal("only this", cmd!.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("looks good to me")]
    [InlineData("fp")]                       // kein Mention
    [InlineData("@naudit please fix")]       // kein fp-Token
    [InlineData("@naudit fpx it")]           // Wortgrenze: fpx != fp
    [InlineData("thanks @naudit fp")]        // Mention nicht am Zeilenanfang
    public void TryParse_returnsNull_forNonCommand(string? body)
        => Assert.Null(FpReplyCommand.TryParse(body));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter FpReplyCommandTests`
Expected: FAIL — `FpReplyCommand` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/Naudit.Infrastructure/Git/FpReplyCommand.cs
using System.Text.RegularExpressions;

namespace Naudit.Infrastructure.Git;

/// <summary>Ein erkanntes FP-Antwort-Kommando: der Grund (Rest der Zeile) oder null.</summary>
public sealed record ParsedFpCommand(string? Reason);

/// <summary>Parst die Antwort auf einen Inline-Kommentar: "@naudit fp|false-positive &lt;grund&gt;"
/// (case-insensitiv, am Zeilenanfang). Rest der ersten Zeile = Grund. Kein Match ⇒ null.
/// Plattform-agnostisch — GitHub- wie GitLab-Kommentar-Bodies laufen hier durch.</summary>
public static class FpReplyCommand
{
    // ^ am (getrimmten) Zeilenanfang; fp|false-positive als ganzes Wort (\b); Rest = Grund.
    private static readonly Regex Pattern = new(
        @"^@naudit\s+(?:fp|false-positive)\b[ \t]*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ParsedFpCommand? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        // Nur die erste Zeile betrachten — das Kommando steht vorn; ein etwaiger Grund ist der Rest DIESER Zeile.
        var line = body.Trim();
        var nl = line.IndexOf('\n');
        if (nl >= 0)
            line = line[..nl];
        line = line.TrimEnd('\r').Trim();

        var m = Pattern.Match(line);
        if (!m.Success)
            return null;

        var reason = m.Groups[1].Value.Trim();
        return new ParsedFpCommand(reason.Length == 0 ? null : reason);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter FpReplyCommandTests`
Expected: PASS (all theories/facts green).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Git/FpReplyCommand.cs tests/Naudit.Tests/FpReplyCommandTests.cs
git commit -m "feat(memory): FP-Antwort-Kommando-Parser (@naudit fp <grund>)"
```

---

### Task 2: Normalized reply DTO + GitHub comment-event mapper

**Files:**
- Create: `src/Naudit.Infrastructure/Git/ReviewCommentReply.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubDtos.cs` (append comment-event DTOs)
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubWebhook.cs` (add `ToCommentReply`)
- Test: `tests/Naudit.Tests/GitHubWebhookTests.cs` (append)

**Interfaces:**
- Consumes: `FpReplyCommand.TryParse` (Task 1); existing `GitHubRepository` (`full_name`), `GitHubUser` (`login`).
- Produces:
  - `record ReviewCommentReply(string ProjectId, int MergeRequestIid, string ReplyToCommentId, string? Reason, string AuthorLogin, string? AuthorAssociation, long? AuthorId)` in namespace `Naudit.Infrastructure.Git`.
  - `GitHubWebhook.ToCommentReply(string? eventType, GitHubReviewCommentEvent payload) -> ReviewCommentReply?`.
  - DTO `GitHubReviewCommentEvent` (+ nested `GitHubReviewCommentPayload`, `GitHubPullRequestRef`).

- [ ] **Step 1: Write the failing test** (append to `GitHubWebhookTests.cs`; add `using Naudit.Infrastructure.Git;` if absent)

```csharp
[Fact]
public void ToCommentReply_mapsFpReply_onReviewComment()
{
    var payload = new GitHubReviewCommentEvent
    {
        Action = "created",
        Repository = new GitHubRepository { FullName = "acme/widgets" },
        PullRequest = new GitHubPullRequestRef { Number = 7 },
        Comment = new GitHubReviewCommentPayload
        {
            Id = 999,
            InReplyToId = 555,
            Body = "@naudit fp intended",
            User = new GitHubUser { Login = "alice" },
            AuthorAssociation = "MEMBER",
        },
    };

    var reply = GitHubWebhook.ToCommentReply("pull_request_review_comment", payload);

    Assert.NotNull(reply);
    Assert.Equal("acme/widgets", reply!.ProjectId);
    Assert.Equal(7, reply.MergeRequestIid);
    Assert.Equal("555", reply.ReplyToCommentId);   // in_reply_to_id → matcht PlatformCommentId
    Assert.Equal("intended", reply.Reason);
    Assert.Equal("alice", reply.AuthorLogin);
    Assert.Equal("MEMBER", reply.AuthorAssociation);
    Assert.Null(reply.AuthorId);                   // GitHub liefert author_association, keine numerische Id
}

[Theory]
[InlineData("issue_comment")]          // falscher Event-Typ
[InlineData("pull_request_review_comment")]
public void ToCommentReply_null_whenNotACommand(string eventType)
{
    var payload = new GitHubReviewCommentEvent
    {
        Action = "created",
        Repository = new GitHubRepository { FullName = "acme/widgets" },
        PullRequest = new GitHubPullRequestRef { Number = 7 },
        Comment = new GitHubReviewCommentPayload { InReplyToId = 555, Body = "looks fine", AuthorAssociation = "MEMBER" },
    };
    Assert.Null(GitHubWebhook.ToCommentReply(eventType, payload));
}

[Fact]
public void ToCommentReply_null_whenTopLevelComment_noInReplyTo()
{
    var payload = new GitHubReviewCommentEvent
    {
        Action = "created",
        Repository = new GitHubRepository { FullName = "acme/widgets" },
        PullRequest = new GitHubPullRequestRef { Number = 7 },
        Comment = new GitHubReviewCommentPayload { InReplyToId = null, Body = "@naudit fp", AuthorAssociation = "MEMBER" },
    };
    Assert.Null(GitHubWebhook.ToCommentReply("pull_request_review_comment", payload));
}

[Fact]
public void ToCommentReply_null_whenActionNotCreated()
{
    var payload = new GitHubReviewCommentEvent
    {
        Action = "edited",
        Repository = new GitHubRepository { FullName = "acme/widgets" },
        PullRequest = new GitHubPullRequestRef { Number = 7 },
        Comment = new GitHubReviewCommentPayload { InReplyToId = 555, Body = "@naudit fp", AuthorAssociation = "MEMBER" },
    };
    Assert.Null(GitHubWebhook.ToCommentReply("pull_request_review_comment", payload));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubWebhookTests`
Expected: FAIL — `ReviewCommentReply` / `GitHubReviewCommentEvent` / `ToCommentReply` do not exist.

- [ ] **Step 3a: Create the normalized DTO**

```csharp
// src/Naudit.Infrastructure/Git/ReviewCommentReply.cs
namespace Naudit.Infrastructure.Git;

/// <summary>Plattform-neutrale Antwort auf einen Naudit-Inline-Kommentar mit FP-Kommando.
/// <paramref name="ReplyToCommentId"/> ist die Plattform-Id des URSPRÜNGLICHEN Kommentars
/// (GitHub in_reply_to_id / GitLab discussion_id) und matcht <c>ReviewFindingEntity.PlatformCommentId</c>.
/// Autorisierungs-Signal ist plattform-spezifisch: GitHub liefert <paramref name="AuthorAssociation"/>
/// direkt in der Payload, GitLab braucht <paramref name="AuthorId"/> für einen Mitglieds-Lookup.</summary>
public sealed record ReviewCommentReply(
    string ProjectId,
    int MergeRequestIid,
    string ReplyToCommentId,
    string? Reason,
    string AuthorLogin,
    string? AuthorAssociation,
    long? AuthorId);
```

- [ ] **Step 3b: Append GitHub comment-event DTOs to `GitHubDtos.cs`**

```csharp
/// <summary>Payload von pull_request_review_comment — die Antwort auf einen Review-Kommentar.</summary>
public sealed class GitHubReviewCommentEvent
{
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("comment")] public GitHubReviewCommentPayload? Comment { get; set; }
    [JsonPropertyName("pull_request")] public GitHubPullRequestRef? PullRequest { get; set; }
    [JsonPropertyName("repository")] public GitHubRepository? Repository { get; set; }
}

public sealed class GitHubReviewCommentPayload
{
    [JsonPropertyName("id")] public long Id { get; set; }
    // Nur Antworten tragen in_reply_to_id; es zeigt auf den Wurzel-Kommentar des Threads (= unsere Finding-Id).
    [JsonPropertyName("in_reply_to_id")] public long? InReplyToId { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("user")] public GitHubUser? User { get; set; }
    [JsonPropertyName("author_association")] public string? AuthorAssociation { get; set; }
}

public sealed class GitHubPullRequestRef
{
    [JsonPropertyName("number")] public int Number { get; set; }
}
```

- [ ] **Step 3c: Add `ToCommentReply` to `GitHubWebhook.cs`** (add `using Naudit.Infrastructure.Git;` — same namespace, no using needed; `ReviewCommentReply` is in `Naudit.Infrastructure.Git`, `GitHubWebhook` is in `Naudit.Infrastructure.Git.GitHub`, so add `using Naudit.Infrastructure.Git;`)

```csharp
    /// <summary>Mappt ein pull_request_review_comment-Event auf ein FP-Kommando, oder null wenn es
    /// keine Antwort auf einen bestehenden Kommentar mit gültigem "@naudit fp"-Body ist.</summary>
    public static ReviewCommentReply? ToCommentReply(string? eventType, GitHubReviewCommentEvent payload)
    {
        if (eventType != "pull_request_review_comment")
            return null;
        if (payload.Action != "created")
            return null;
        // Nur Antworten (in_reply_to_id gesetzt) — sie zeigen auf den Wurzel-Kommentar, unter dem Naudits
        // Finding hängt. Top-Level-Kommentare ohne in_reply_to_id lassen sich keinem Finding zuordnen.
        if (payload.Comment?.InReplyToId is not long replyTo)
            return null;
        if (payload.Repository?.FullName is not string repo)
            return null;
        if (payload.PullRequest is not { } pr)
            return null;

        var cmd = FpReplyCommand.TryParse(payload.Comment.Body);
        if (cmd is null)
            return null;

        return new ReviewCommentReply(repo, pr.Number, replyTo.ToString(), cmd.Reason,
            payload.Comment.User?.Login ?? "", payload.Comment.AuthorAssociation, AuthorId: null);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubWebhookTests`
Expected: PASS (new + existing GitHub webhook tests green).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Git/ReviewCommentReply.cs src/Naudit.Infrastructure/Git/GitHub/GitHubDtos.cs src/Naudit.Infrastructure/Git/GitHub/GitHubWebhook.cs tests/Naudit.Tests/GitHubWebhookTests.cs
git commit -m "feat(memory): GitHub-Comment-Event → ReviewCommentReply (FP-Kommando)"
```

---

### Task 3: GitLab note-event mapper

**Files:**
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabDtos.cs` (append note-event DTOs)
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabWebhook.cs` (add `ToCommentReply`)
- Test: `tests/Naudit.Tests/GitLabWebhookTests.cs` (append)

**Interfaces:**
- Consumes: `FpReplyCommand.TryParse` (Task 1); `ReviewCommentReply` (Task 2); existing `GitLabProject` (`id`).
- Produces: `GitLabWebhook.ToCommentReply(GitLabNoteEvent payload) -> ReviewCommentReply?`; DTO `GitLabNoteEvent` (+ `GitLabNoteUser`, `GitLabNoteAttributes`, `GitLabNoteMergeRequest`).

- [ ] **Step 1: Write the failing test** (append to `GitLabWebhookTests.cs`; add `using Naudit.Infrastructure.Git;` if absent)

```csharp
[Fact]
public void ToCommentReply_mapsFpReply_onMergeRequestNote()
{
    var payload = new GitLabNoteEvent
    {
        ObjectKind = "note",
        User = new GitLabNoteUser { Id = 42, Username = "bob" },
        Project = new GitLabProject { Id = 7 },
        MergeRequest = new GitLabNoteMergeRequest { Iid = 13 },
        ObjectAttributes = new GitLabNoteAttributes
        {
            Note = "@naudit fp legacy pattern",
            NoteableType = "MergeRequest",
            DiscussionId = "abc123",
        },
    };

    var reply = GitLabWebhook.ToCommentReply(payload);

    Assert.NotNull(reply);
    Assert.Equal("7", reply!.ProjectId);
    Assert.Equal(13, reply.MergeRequestIid);
    Assert.Equal("abc123", reply.ReplyToCommentId);  // discussion_id → matcht PlatformCommentId
    Assert.Equal("legacy pattern", reply.Reason);
    Assert.Equal("bob", reply.AuthorLogin);
    Assert.Null(reply.AuthorAssociation);            // GitLab: keine Association
    Assert.Equal(42, reply.AuthorId);                // GitLab: user.id für den Mitglieds-Lookup
}

[Fact]
public void ToCommentReply_null_whenNoteableNotMergeRequest()
{
    var payload = new GitLabNoteEvent
    {
        ObjectKind = "note",
        User = new GitLabNoteUser { Id = 42, Username = "bob" },
        Project = new GitLabProject { Id = 7 },
        MergeRequest = new GitLabNoteMergeRequest { Iid = 13 },
        ObjectAttributes = new GitLabNoteAttributes { Note = "@naudit fp", NoteableType = "Issue", DiscussionId = "abc123" },
    };
    Assert.Null(GitLabWebhook.ToCommentReply(payload));
}

[Fact]
public void ToCommentReply_null_whenNotACommand()
{
    var payload = new GitLabNoteEvent
    {
        ObjectKind = "note",
        User = new GitLabNoteUser { Id = 42, Username = "bob" },
        Project = new GitLabProject { Id = 7 },
        MergeRequest = new GitLabNoteMergeRequest { Iid = 13 },
        ObjectAttributes = new GitLabNoteAttributes { Note = "thanks, merging", NoteableType = "MergeRequest", DiscussionId = "abc123" },
    };
    Assert.Null(GitLabWebhook.ToCommentReply(payload));
}

[Fact]
public void ToCommentReply_null_whenDiscussionIdMissing()
{
    var payload = new GitLabNoteEvent
    {
        ObjectKind = "note",
        User = new GitLabNoteUser { Id = 42, Username = "bob" },
        Project = new GitLabProject { Id = 7 },
        MergeRequest = new GitLabNoteMergeRequest { Iid = 13 },
        ObjectAttributes = new GitLabNoteAttributes { Note = "@naudit fp", NoteableType = "MergeRequest", DiscussionId = null },
    };
    Assert.Null(GitLabWebhook.ToCommentReply(payload));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitLabWebhookTests`
Expected: FAIL — `GitLabNoteEvent` / `ToCommentReply` do not exist.

- [ ] **Step 3a: Append note-event DTOs to `GitLabDtos.cs`**

```csharp
/// <summary>Payload eines GitLab Note-Hooks (object_kind: note) — die Antwort/der Kommentar.</summary>
public sealed class GitLabNoteEvent
{
    [JsonPropertyName("object_kind")] public string? ObjectKind { get; set; }
    [JsonPropertyName("user")] public GitLabNoteUser? User { get; set; }
    [JsonPropertyName("project")] public GitLabProject? Project { get; set; }
    [JsonPropertyName("object_attributes")] public GitLabNoteAttributes? ObjectAttributes { get; set; }
    [JsonPropertyName("merge_request")] public GitLabNoteMergeRequest? MergeRequest { get; set; }
}

public sealed class GitLabNoteUser
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
}

public sealed class GitLabNoteAttributes
{
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("noteable_type")] public string? NoteableType { get; set; }
    // Discussion-Id der Antwort — identisch zur Discussion, in der Naudits Finding-Kommentar steckt.
    [JsonPropertyName("discussion_id")] public string? DiscussionId { get; set; }
}

public sealed class GitLabNoteMergeRequest
{
    [JsonPropertyName("iid")] public int Iid { get; set; }
}
```

- [ ] **Step 3b: Add `ToCommentReply` to `GitLabWebhook.cs`** (add `using Naudit.Infrastructure.Git;`)

```csharp
    /// <summary>Mappt ein GitLab-Note-Event auf ein FP-Kommando, oder null wenn es keine
    /// MergeRequest-Antwort mit gültigem "@naudit fp"-Body und Discussion-Id ist.</summary>
    public static ReviewCommentReply? ToCommentReply(GitLabNoteEvent payload)
    {
        if (payload.ObjectKind != "note")
            return null;
        var attrs = payload.ObjectAttributes;
        if (attrs?.NoteableType != "MergeRequest")
            return null;
        if (string.IsNullOrEmpty(attrs.DiscussionId))
            return null;
        if (payload.MergeRequest is not { } mr)
            return null;
        if (payload.Project is not { } project)
            return null;
        if (payload.User is not { } user)
            return null;

        var cmd = FpReplyCommand.TryParse(attrs.Note);
        if (cmd is null)
            return null;

        return new ReviewCommentReply(project.Id.ToString(), mr.Iid, attrs.DiscussionId, cmd.Reason,
            user.Username ?? "", AuthorAssociation: null, user.Id);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitLabWebhookTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Git/GitLab/GitLabDtos.cs src/Naudit.Infrastructure/Git/GitLab/GitLabWebhook.cs tests/Naudit.Tests/GitLabWebhookTests.cs
git commit -m "feat(memory): GitLab-Note-Event → ReviewCommentReply (FP-Kommando)"
```

---

### Task 4: `IReviewCommentResponder` seam + GitHub implementation

**Files:**
- Create: `src/Naudit.Infrastructure/Git/IReviewCommentResponder.cs`
- Create: `src/Naudit.Infrastructure/Git/GitHub/GitHubCommentResponder.cs`
- Test: `tests/Naudit.Tests/GitHubCommentResponderTests.cs`

**Interfaces:**
- Consumes: `ReviewCommentReply` (Task 2); existing `IGitTokenProvider.ResolveTokenAsync`.
- Produces:
  - `interface IReviewCommentResponder { Task<bool> IsAuthorizedAsync(ReviewCommentReply, CancellationToken); Task PostReplyAsync(ReviewCommentReply, string body, CancellationToken); }` (namespace `Naudit.Infrastructure.Git`).
  - `GitHubCommentResponder(HttpClient, IGitTokenProvider)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Naudit.Tests/GitHubCommentResponderTests.cs
using System.Net;
using System.Text;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitHubCommentResponderTests
{
    private static IGitTokenProvider Tokens() =>
        new ConfiguredGitTokenProvider("tok", System.Array.Empty<ProjectTokenEntry>());

    private static ReviewCommentReply Reply(string? association) =>
        new("acme/widgets", 7, "555", "reason", "alice", association, AuthorId: null);

    [Theory]
    [InlineData("OWNER", true)]
    [InlineData("MEMBER", true)]
    [InlineData("COLLABORATOR", true)]
    [InlineData("member", true)]            // case-insensitiv
    [InlineData("CONTRIBUTOR", false)]
    [InlineData("NONE", false)]
    [InlineData("FIRST_TIME_CONTRIBUTOR", false)]
    [InlineData(null, false)]
    public async Task IsAuthorizedAsync_gatesOnAuthorAssociation(string? association, bool expected)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var responder = new GitHubCommentResponder(
            new HttpClient(handler) { BaseAddress = new System.Uri("https://api.github.com/") }, Tokens());

        Assert.Equal(expected, await responder.IsAuthorizedAsync(Reply(association)));
        Assert.Empty(handler.Calls);   // reine Payload-Prüfung, kein API-Call
    }

    [Fact]
    public async Task PostReplyAsync_postsToRepliesEndpoint_withBearer()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var responder = new GitHubCommentResponder(
            new HttpClient(handler) { BaseAddress = new System.Uri("https://api.github.com/") }, Tokens());

        await responder.PostReplyAsync(Reply("MEMBER"), "Als False Positive gemerkt.");

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://api.github.com/repos/acme/widgets/pulls/7/comments/555/replies", call.Uri!.ToString());
        Assert.Contains("Als False Positive gemerkt.", call.Body);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("tok", handler.Requests[0].Headers.Authorization!.Parameter);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubCommentResponderTests`
Expected: FAIL — types missing.

- [ ] **Step 3a: Create the interface**

```csharp
// src/Naudit.Infrastructure/Git/IReviewCommentResponder.cs
namespace Naudit.Infrastructure.Git;

/// <summary>Plattform-Fähigkeit für das FP-Antwort-Kommando: Autor autorisieren + Bestätigung
/// im Thread posten. Bewusst eine INFRASTRUKTUR-Naht (nicht IGitPlatform/Core) — das Kommando
/// ist rein Infrastructure/Web. Eine Implementierung je Plattform, per Config gewählt.</summary>
public interface IReviewCommentResponder
{
    /// <summary>Darf dieser Autor Findings als False Positive markieren? Fail-closed:
    /// unverifizierbar ⇒ false. GitHub prüft author_association (kein I/O), GitLab die Mitgliedschaft.</summary>
    Task<bool> IsAuthorizedAsync(ReviewCommentReply reply, CancellationToken ct = default);

    /// <summary>Postet die Bestätigung als Antwort in denselben Thread/dieselbe Discussion.</summary>
    Task PostReplyAsync(ReviewCommentReply reply, string body, CancellationToken ct = default);
}
```

- [ ] **Step 3b: Create the GitHub implementation**

```csharp
// src/Naudit.Infrastructure/Git/GitHub/GitHubCommentResponder.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Naudit.Infrastructure.Git.GitHub;

/// <summary>GitHub-Umsetzung des FP-Antwort-Kommandos. Autorisierung ganz aus der Payload
/// (author_association — kein API-Call); Bestätigung als Reply auf den Review-Kommentar.</summary>
public sealed class GitHubCommentResponder(HttpClient http, IGitTokenProvider tokens) : IReviewCommentResponder
{
    // Wer als OWNER/MEMBER/COLLABORATOR kommentiert, gehört zum Repo — fail-closed für alles andere.
    private static readonly HashSet<string> Allowed =
        new(StringComparer.OrdinalIgnoreCase) { "OWNER", "MEMBER", "COLLABORATOR" };

    public Task<bool> IsAuthorizedAsync(ReviewCommentReply reply, CancellationToken ct = default)
        => Task.FromResult(reply.AuthorAssociation is { } a && Allowed.Contains(a));

    public async Task PostReplyAsync(ReviewCommentReply reply, string body, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"repos/{reply.ProjectId}/pulls/{reply.MergeRequestIid}/comments/{reply.ReplyToCommentId}/replies");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.ResolveTokenAsync(reply.ProjectId, ct));
        req.Content = JsonContent.Create(new { body });
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubCommentResponderTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Git/IReviewCommentResponder.cs src/Naudit.Infrastructure/Git/GitHub/GitHubCommentResponder.cs tests/Naudit.Tests/GitHubCommentResponderTests.cs
git commit -m "feat(memory): IReviewCommentResponder-Naht + GitHub-Impl (Auth via author_association, Reply)"
```

---

### Task 5: GitLab `IReviewCommentResponder` implementation

**Files:**
- Create: `src/Naudit.Infrastructure/Git/GitLab/GitLabCommentResponder.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabDtos.cs` (append `GitLabMember`)
- Test: `tests/Naudit.Tests/GitLabCommentResponderTests.cs`

**Interfaces:**
- Consumes: `IReviewCommentResponder` (Task 4); `ReviewCommentReply` (Task 2); `IGitTokenProvider`.
- Produces: `GitLabCommentResponder(HttpClient, IGitTokenProvider)`; `record GitLabMember(int AccessLevel)`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Naudit.Tests/GitLabCommentResponderTests.cs
using System.Net;
using System.Text;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitLabCommentResponderTests
{
    private static IGitTokenProvider Tokens() =>
        new ConfiguredGitTokenProvider("tok", System.Array.Empty<ProjectTokenEntry>());

    private static ReviewCommentReply Reply(long? authorId = 42) =>
        new("7", 13, "abc123", "reason", "bob", AuthorAssociation: null, authorId);

    private static GitLabCommentResponder Responder(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new System.Uri("https://gitlab.example.com/") }, Tokens());

    [Theory]
    [InlineData(50, true)]   // Owner
    [InlineData(40, true)]   // Maintainer
    [InlineData(30, true)]   // Developer (Schwelle)
    [InlineData(20, false)]  // Reporter
    [InlineData(10, false)]  // Guest
    public async Task IsAuthorizedAsync_requiresDeveloperOrAbove(int accessLevel, bool expected)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{ "id": 42, "access_level": {{accessLevel}} }""", Encoding.UTF8, "application/json"),
        });

        Assert.Equal(expected, await Responder(handler).IsAuthorizedAsync(Reply()));
        Assert.Equal("https://gitlab.example.com/api/v4/projects/7/members/all/42", handler.Calls[0].Uri!.ToString());
        Assert.Equal("tok", handler.Requests[0].Headers.GetValues("PRIVATE-TOKEN").Single());
    }

    [Fact]
    public async Task IsAuthorizedAsync_false_whenNotAMember_404()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.False(await Responder(handler).IsAuthorizedAsync(Reply()));
    }

    [Fact]
    public async Task IsAuthorizedAsync_false_whenAuthorIdMissing()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.False(await Responder(handler).IsAuthorizedAsync(Reply(authorId: null)));
        Assert.Empty(handler.Calls);   // ohne Id kein Lookup
    }

    [Fact]
    public async Task PostReplyAsync_addsNoteToDiscussion_withPrivateToken()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        await Responder(handler).PostReplyAsync(Reply(), "Als False Positive gemerkt.");

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://gitlab.example.com/api/v4/projects/7/merge_requests/13/discussions/abc123/notes", call.Uri!.ToString());
        Assert.Contains("Als False Positive gemerkt.", call.Body);
        Assert.Equal("tok", handler.Requests[0].Headers.GetValues("PRIVATE-TOKEN").Single());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitLabCommentResponderTests`
Expected: FAIL — types missing.

- [ ] **Step 3a: Append `GitLabMember` DTO to `GitLabDtos.cs`**

```csharp
/// <summary>Ein Projekt-Mitglied aus GET …/members/all/{user_id} — nur das Zugriffslevel zählt
/// (Developer=30, Maintainer=40, Owner=50).</summary>
public sealed record GitLabMember([property: JsonPropertyName("access_level")] int AccessLevel);
```

- [ ] **Step 3b: Create the GitLab implementation**

```csharp
// src/Naudit.Infrastructure/Git/GitLab/GitLabCommentResponder.cs
using System.Net.Http.Json;
using System.Text.Json;

namespace Naudit.Infrastructure.Git.GitLab;

/// <summary>GitLab-Umsetzung des FP-Antwort-Kommandos. Autorisierung über die effektive
/// Mitgliedschaft (members/all, Access-Level ≥ Developer); Bestätigung als Note in der Discussion.</summary>
public sealed class GitLabCommentResponder(HttpClient http, IGitTokenProvider tokens) : IReviewCommentResponder
{
    private const int DeveloperAccessLevel = 30;

    public async Task<bool> IsAuthorizedAsync(ReviewCommentReply reply, CancellationToken ct = default)
    {
        if (reply.AuthorId is not long userId)
            return false;   // ohne Autor-Id nicht verifizierbar ⇒ fail-closed

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"api/v4/projects/{reply.ProjectId}/members/all/{userId}");
        req.Headers.Add("PRIVATE-TOKEN", await tokens.ResolveTokenAsync(reply.ProjectId, ct));
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            return false;   // 404 = kein Mitglied, alles andere = im Zweifel nein (fail-closed)

        try
        {
            var member = await res.Content.ReadFromJsonAsync<GitLabMember>(ct);
            return member is { AccessLevel: >= DeveloperAccessLevel };
        }
        catch (JsonException)
        {
            return false;   // leerer/unerwarteter Body ⇒ fail-closed
        }
    }

    public async Task PostReplyAsync(ReviewCommentReply reply, string body, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"api/v4/projects/{reply.ProjectId}/merge_requests/{reply.MergeRequestIid}/discussions/{reply.ReplyToCommentId}/notes");
        req.Headers.Add("PRIVATE-TOKEN", await tokens.ResolveTokenAsync(reply.ProjectId, ct));
        req.Content = JsonContent.Create(new { body });
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitLabCommentResponderTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Git/GitLab/GitLabCommentResponder.cs src/Naudit.Infrastructure/Git/GitLab/GitLabDtos.cs tests/Naudit.Tests/GitLabCommentResponderTests.cs
git commit -m "feat(memory): GitLab-Impl von IReviewCommentResponder (Mitglieds-Lookup ≥ Developer, Note-Reply)"
```

---

### Task 6: Extract `MemoryEntryWriter` (shared idempotent FP upsert)

**Files:**
- Create: `src/Naudit.Infrastructure/Memory/MemoryEntryWriter.cs`
- Modify: `src/Naudit.Web/Endpoints/MemoryEndpoints.cs` (POST route calls the writer)
- Test: `tests/Naudit.Tests/MemoryEntryWriterTests.cs`

**Rationale:** the WebUI FP button and the reply command must create the *exact same* entry with the same idempotency/race handling. Extract the one upsert so both callers share it (DRY, one source of truth for the security-relevant idempotency). Existing `MemoryEndpointTests` must stay green (behaviour identical).

**Interfaces:**
- Consumes: `NauditDbContext`, `ReviewFindingEntity` (with `Review` navigation loaded), EF Core.
- Produces: `static Task<MemoryEntryEntity> MemoryEntryWriter.MarkFalsePositiveAsync(NauditDbContext db, ReviewFindingEntity finding, string? reason, string createdBy, CancellationToken ct = default)` (namespace `Naudit.Infrastructure.Memory`).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Naudit.Tests/MemoryEntryWriterTests.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Memory;
using Xunit;

namespace Naudit.Tests;

public class MemoryEntryWriterTests
{
    private static NauditDbContext NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(conn).Options;
        var db = new NauditDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    private static async Task<ReviewFindingEntity> SeedFindingAsync(NauditDbContext db)
    {
        var project = new ProjectEntity { PlatformProjectId = "acme/widgets", FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow };
        var review = new ReviewEntity { Project = project, PrNumber = 7, Title = "t", Verdict = "approve", Summary = "s", CreatedAt = DateTime.UtcNow };
        var finding = new ReviewFindingEntity { Review = review, Severity = "medium", Confidence = "high", File = "src/Foo.cs", Line = 3, Text = "flag", PlatformCommentId = "555" };
        db.ReviewFindings.Add(finding);
        await db.SaveChangesAsync();
        return finding;
    }

    [Fact]
    public async Task MarkFalsePositiveAsync_createsEntryFromFinding()
    {
        using var db = NewDb();
        var finding = await SeedFindingAsync(db);

        var entry = await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, "because legacy", "bob");

        Assert.Equal("FalsePositive", entry.Kind);
        Assert.Equal("src/Foo.cs", entry.File);
        Assert.Equal("flag", entry.Text);
        Assert.Equal(finding.Id, entry.SourceFindingId);
        Assert.Equal("because legacy", entry.Reason);
        Assert.Equal("bob", entry.CreatedBy);
        Assert.True(entry.Active);
    }

    [Fact]
    public async Task MarkFalsePositiveAsync_isIdempotent_reactivatesAndUpdatesReason()
    {
        using var db = NewDb();
        var finding = await SeedFindingAsync(db);

        var first = await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, null, "bob");
        first.Active = false;                       // simuliere ein zwischenzeitliches Undo
        await db.SaveChangesAsync();

        var second = await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, "now with reason", "carol");

        Assert.Equal(first.Id, second.Id);          // kein Duplikat
        Assert.True(second.Active);                 // reaktiviert
        Assert.Equal("now with reason", second.Reason);
        Assert.Equal(1, await db.MemoryEntries.CountAsync());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter MemoryEntryWriterTests`
Expected: FAIL — `MemoryEntryWriter` does not exist.

- [ ] **Step 3a: Create `MemoryEntryWriter`** (moves the exact idempotent upsert from `MemoryEndpoints`, incl. the `DbUpdateException` race retry)

```csharp
// src/Naudit.Infrastructure/Memory/MemoryEntryWriter.cs
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Memory;

/// <summary>Gemeinsamer, idempotenter FP-Upsert aus einem Finding — genutzt von der WebUI (FP-Button)
/// UND vom "@naudit fp"-Antwort-Kommando. Anker ist SourceFindingId (unique unter nicht-null):
/// existiert der Eintrag, wird er reaktiviert/aktualisiert statt dupliziert. Das Doppel-POST-Race auf
/// dem Unique-Index (DbUpdateException) wird idempotent aufgelöst statt in einen 500 zu laufen.</summary>
public static class MemoryEntryWriter
{
    public static async Task<MemoryEntryEntity> MarkFalsePositiveAsync(
        NauditDbContext db, ReviewFindingEntity finding, string? reason, string createdBy, CancellationToken ct = default)
    {
        var entry = await db.MemoryEntries.SingleOrDefaultAsync(m => m.SourceFindingId == finding.Id, ct);
        if (entry is null)
        {
            entry = new MemoryEntryEntity
            {
                ProjectId = finding.Review.ProjectId,
                Kind = "FalsePositive",
                File = finding.File,
                Text = finding.Text,
                SourceFindingId = finding.Id,
                CreatedBy = createdBy,
                CreatedAt = DateTime.UtcNow,
                Active = true,
            };
            db.MemoryEntries.Add(entry);
        }
        entry.Active = true;
        if (!string.IsNullOrWhiteSpace(reason))
            entry.Reason = reason.Trim();

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (entry.Id == 0)
        {
            // Race mit parallelem Markieren: beide sahen entry==null, der andere legte zuerst an —
            // der Unique-Index lässt unser Insert scheitern. Idempotent behandeln.
            db.ChangeTracker.Clear();
            entry = await db.MemoryEntries.SingleAsync(m => m.SourceFindingId == finding.Id, ct);
            entry.Active = true;
            if (!string.IsNullOrWhiteSpace(reason))
                entry.Reason = reason.Trim();
            await db.SaveChangesAsync(ct);
        }
        return entry;
    }
}
```

- [ ] **Step 3b: Refactor `MemoryEndpoints` POST to use the writer.** Replace the inline block (from `var finding = ...` idempotency comment through the `catch (DbUpdateException) ... }` block) so the route reads:

```csharp
        api.MapPost("/findings/{id:int}/false-positive", async (HttpContext ctx, NauditDbContext db, int id, FpBody? body) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            if (body?.Reason is { Length: > MaxFreeTextLength })
                return Results.BadRequest(new { error = $"reason must not exceed {MaxFreeTextLength} characters" });

            var finding = await db.ReviewFindings.Include(f => f.Review)
                .SingleOrDefaultAsync(f => f.Id == id, ctx.RequestAborted);
            if (finding is null) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, finding.Review.ProjectId, ctx.RequestAborted))
                return Results.Forbid();

            var entry = await Naudit.Infrastructure.Memory.MemoryEntryWriter.MarkFalsePositiveAsync(
                db, finding, body?.Reason, acct.Username, ctx.RequestAborted);
            return Results.Ok(new { id = entry.Id, active = entry.Active });
        });
```

- [ ] **Step 4: Run tests to verify green** (writer unit tests **and** the untouched endpoint behaviour)

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "MemoryEntryWriterTests|MemoryEndpointTests"`
Expected: PASS (both classes).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Memory/MemoryEntryWriter.cs src/Naudit.Web/Endpoints/MemoryEndpoints.cs tests/Naudit.Tests/MemoryEntryWriterTests.cs
git commit -m "refactor(memory): idempotenten FP-Upsert in MemoryEntryWriter herausgezogen (WebUI + Kommando teilen ihn)"
```

---

### Task 7: `ReviewCommentCommandService` (orchestrator)

**Files:**
- Create: `src/Naudit.Infrastructure/Memory/ReviewCommentCommandService.cs`
- Test: `tests/Naudit.Tests/ReviewCommentCommandServiceTests.cs`

**Interfaces:**
- Consumes: `NauditDbContext`; `IReviewCommentResponder` (Task 4); `MemoryEntryWriter` (Task 6); `ReviewCommentReply` (Task 2); `ILogger`.
- Produces: `ReviewCommentCommandService(NauditDbContext, IReviewCommentResponder, ILogger<ReviewCommentCommandService>)` with `Task HandleAsync(ReviewCommentReply reply, CancellationToken ct = default)` and `const string ConfirmationText = "Als False Positive gemerkt.";`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Naudit.Tests/ReviewCommentCommandServiceTests.cs
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Memory;
using Xunit;

namespace Naudit.Tests;

public class ReviewCommentCommandServiceTests
{
    // Fake-Responder: konfigurierbare Autorisierung, zeichnet gepostete Antworten auf.
    private sealed class FakeResponder(bool authorized) : IReviewCommentResponder
    {
        public List<string> Replies { get; } = new();
        public Task<bool> IsAuthorizedAsync(ReviewCommentReply reply, CancellationToken ct = default) => Task.FromResult(authorized);
        public Task PostReplyAsync(ReviewCommentReply reply, string body, CancellationToken ct = default)
        { Replies.Add(body); return Task.CompletedTask; }
    }

    private static NauditDbContext NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(conn).Options;
        var db = new NauditDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    // Seedet ein Projekt+Review mit einem Finding, dessen PlatformCommentId gesetzt ist.
    private static async Task<ReviewFindingEntity> SeedAsync(NauditDbContext db, string platformProjectId, string commentId)
    {
        var project = new ProjectEntity { PlatformProjectId = platformProjectId, FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow };
        var review = new ReviewEntity { Project = project, PrNumber = 7, Title = "t", Verdict = "approve", Summary = "s", CreatedAt = DateTime.UtcNow };
        var finding = new ReviewFindingEntity { Review = review, Severity = "medium", Confidence = "high", File = "src/Foo.cs", Line = 3, Text = "flag", PlatformCommentId = commentId };
        db.ReviewFindings.Add(finding);
        await db.SaveChangesAsync();
        return finding;
    }

    private static ReviewCommentReply Reply(string projectId, string commentId, string? reason = "legacy") =>
        new(projectId, 7, commentId, reason, "bob", AuthorAssociation: "MEMBER", AuthorId: 42);

    [Fact]
    public async Task HandleAsync_marksFp_andReplies_whenAuthorizedAndMatched()
    {
        using var db = NewDb();
        var finding = await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true);
        var svc = new ReviewCommentCommandService(db, responder, NullLogger<ReviewCommentCommandService>.Instance);

        await svc.HandleAsync(Reply("acme/widgets", "555"));

        var entry = Assert.Single(db.MemoryEntries);
        Assert.Equal("FalsePositive", entry.Kind);
        Assert.Equal(finding.Id, entry.SourceFindingId);
        Assert.Equal("legacy", entry.Reason);
        Assert.Equal("bob", entry.CreatedBy);
        Assert.Equal(ReviewCommentCommandService.ConfirmationText, Assert.Single(responder.Replies));
    }

    [Fact]
    public async Task HandleAsync_ignores_whenUnauthorized()
    {
        using var db = NewDb();
        await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: false);
        var svc = new ReviewCommentCommandService(db, responder, NullLogger<ReviewCommentCommandService>.Instance);

        await svc.HandleAsync(Reply("acme/widgets", "555"));

        Assert.Empty(db.MemoryEntries);
        Assert.Empty(responder.Replies);
    }

    [Fact]
    public async Task HandleAsync_ignores_whenNoFindingMatches()
    {
        using var db = NewDb();
        await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true);
        var svc = new ReviewCommentCommandService(db, responder, NullLogger<ReviewCommentCommandService>.Instance);

        await svc.HandleAsync(Reply("acme/widgets", "does-not-exist"));

        Assert.Empty(db.MemoryEntries);
        Assert.Empty(responder.Replies);
    }

    [Fact]
    public async Task HandleAsync_scopesByProject_ignoresSameCommentIdInOtherProject()
    {
        using var db = NewDb();
        await SeedAsync(db, "acme/other", "555");   // gleiche Comment-Id, anderes Projekt
        var responder = new FakeResponder(authorized: true);
        var svc = new ReviewCommentCommandService(db, responder, NullLogger<ReviewCommentCommandService>.Instance);

        await svc.HandleAsync(Reply("acme/widgets", "555"));   // Reply gilt "acme/widgets"

        Assert.Empty(db.MemoryEntries);
        Assert.Empty(responder.Replies);
    }

    [Fact]
    public async Task HandleAsync_idempotent_onSecondCall()
    {
        using var db = NewDb();
        await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true);
        var svc = new ReviewCommentCommandService(db, responder, NullLogger<ReviewCommentCommandService>.Instance);

        await svc.HandleAsync(Reply("acme/widgets", "555"));
        await svc.HandleAsync(Reply("acme/widgets", "555"));

        Assert.Equal(1, await db.MemoryEntries.CountAsync());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewCommentCommandServiceTests`
Expected: FAIL — `ReviewCommentCommandService` does not exist.

- [ ] **Step 3: Write the orchestrator**

```csharp
// src/Naudit.Infrastructure/Memory/ReviewCommentCommandService.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;

namespace Naudit.Infrastructure.Memory;

/// <summary>Verarbeitet ein "@naudit fp"-Antwort-Kommando: Autor autorisieren → Antwort dem Finding
/// zuordnen (über die in PR 2a erfasste PlatformCommentId, projekt-gescoped) → FP-Eintrag anlegen →
/// im Thread bestätigen. Alles fail-closed/best-effort — der Webhook antwortet immer 200.</summary>
public sealed class ReviewCommentCommandService(
    NauditDbContext db, IReviewCommentResponder responder, ILogger<ReviewCommentCommandService> logger)
{
    public const string ConfirmationText = "Als False Positive gemerkt.";

    public async Task HandleAsync(ReviewCommentReply reply, CancellationToken ct = default)
    {
        if (!await responder.IsAuthorizedAsync(reply, ct))
        {
            logger.LogInformation("FP-Kommando von {Author} auf {Project}!{Iid} ignoriert — nicht autorisiert.",
                reply.AuthorLogin, reply.ProjectId, reply.MergeRequestIid);
            return;
        }

        // Antwort → Finding: die Comment-Id der Antwort == PlatformCommentId des Findings, projekt-gescoped
        // (Ids kollidieren sonst über Projekte). Kante aus PR 2a: zwei Findings auf derselben Datei+Zeile
        // teilten sich EINE GitHub-Comment-Id — dann deterministisch das erste (kleinste Id) nehmen.
        var findings = await db.ReviewFindings
            .Include(f => f.Review)
            .Where(f => f.PlatformCommentId == reply.ReplyToCommentId
                        && f.Review.Project.PlatformProjectId == reply.ProjectId)
            .OrderBy(f => f.Id)
            .ToListAsync(ct);
        if (findings.Count == 0)
        {
            logger.LogInformation("FP-Kommando auf {Project}!{Iid} ohne zugeordnetes Finding (Comment-Id {Id}) — ignoriert.",
                reply.ProjectId, reply.MergeRequestIid, reply.ReplyToCommentId);
            return;
        }
        if (findings.Count > 1)
            logger.LogWarning("Comment-Id {Id} auf {Project} ist mehrdeutig ({Count} Findings) — erstes gewählt.",
                reply.ReplyToCommentId, reply.ProjectId, findings.Count);

        var finding = findings[0];
        await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, reply.Reason, reply.AuthorLogin, ct);
        logger.LogInformation("Finding {FindingId} auf {Project}!{Iid} von {Author} als False Positive gemerkt.",
            finding.Id, reply.ProjectId, reply.MergeRequestIid, reply.AuthorLogin);

        try
        {
            await responder.PostReplyAsync(reply, ConfirmationText, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Bestätigung ist best-effort — der Eintrag steht bereits; ein fehlgeschlagener Reply
            // darf den Webhook (200) nicht kippen.
            logger.LogWarning(ex, "Bestätigungs-Antwort auf {Project}!{Iid} fehlgeschlagen.",
                reply.ProjectId, reply.MergeRequestIid);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewCommentCommandServiceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Memory/ReviewCommentCommandService.cs tests/Naudit.Tests/ReviewCommentCommandServiceTests.cs
git commit -m "feat(memory): ReviewCommentCommandService — FP-Kommando autorisieren, zuordnen, merken, bestätigen"
```

---

### Task 8: DI registration + webhook endpoint wiring

**Files:**
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (register responder per platform + command service)
- Modify: `src/Naudit.Web/Program.cs` (comment branch in both webhook endpoints)
- Test: `tests/Naudit.Tests/WebhookEndpointTests.cs` (append)

**Interfaces:**
- Consumes: `IReviewCommentResponder` + impls (Tasks 4/5); `ReviewCommentCommandService` (Task 7); `GitHubWebhook.ToCommentReply`/`GitLabWebhook.ToCommentReply` (Tasks 2/3); existing `IAccessGate`.
- Produces: mapped comment handling on `/webhook/github` and `/webhook/gitlab`.

- [ ] **Step 1: Write the failing test** (append to `WebhookEndpointTests.cs`)

```csharp
[Fact]
public async Task GitHubWebhook_reviewComment_nonCommand_returnsOk()
{
    var client = _factory
        .WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitHub");
            b.UseSetting("Naudit:GitHub:WebhookSecret", "gh-secret");
        })
        .CreateClient();

    // Gültige Signatur, aber Body ist KEIN Kommando ⇒ Mapping null ⇒ 200, ohne Plattform-Call.
    const string body = """
        { "action": "created",
          "repository": { "full_name": "acme/widgets" },
          "pull_request": { "number": 7 },
          "comment": { "id": 999, "in_reply_to_id": 555, "body": "thanks!", "author_association": "MEMBER" } }
        """;
    var message = new HttpRequestMessage(HttpMethod.Post, "/webhook/github")
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    message.Headers.Add("X-GitHub-Event", "pull_request_review_comment");
    message.Headers.Add("X-Hub-Signature-256", SignGitHub("gh-secret", body));

    var response = await client.SendAsync(message);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}

[Fact]
public async Task GitHubWebhook_reviewComment_badSignature_returnsUnauthorized()
{
    var client = _factory
        .WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitHub");
            b.UseSetting("Naudit:GitHub:WebhookSecret", "gh-secret");
        })
        .CreateClient();

    const string body = """{ "action": "created", "comment": { "in_reply_to_id": 555, "body": "@naudit fp" } }""";
    var message = new HttpRequestMessage(HttpMethod.Post, "/webhook/github")
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    message.Headers.Add("X-GitHub-Event", "pull_request_review_comment");
    message.Headers.Add("X-Hub-Signature-256", SignGitHub("wrong-secret", body));

    var response = await client.SendAsync(message);
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);   // Signatur vor Comment-Handling
}

[Fact]
public async Task GitLabWebhook_note_nonCommand_returnsOk()
{
    var client = _factory
        .WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitLab");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "test-secret");
        })
        .CreateClient();

    var message = new HttpRequestMessage(HttpMethod.Post, "/webhook/gitlab")
    {
        Content = JsonContent.Create(new
        {
            object_kind = "note",
            user = new { id = 42, username = "bob" },
            project = new { id = 7 },
            merge_request = new { iid = 13 },
            object_attributes = new { note = "thanks", noteable_type = "MergeRequest", discussion_id = "abc123" },
        }),
    };
    message.Headers.Add("X-Gitlab-Token", "test-secret");

    var response = await client.SendAsync(message);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter WebhookEndpointTests`
Expected: FAIL — GitHub `pull_request_review_comment` currently falls through to `ToReviewRequest` (returns null → 200) so the *non-command* GitHub test may already pass, but the GitLab `note` test fails (current code maps `object_kind:note` via `ToReviewRequest` → null → 200 → actually also passes). **These endpoint tests mainly lock in wiring;** the real red is at compile time once Step 3 references new symbols. If all three pass before Step 3, that only proves the endpoints don't 500 on the new events — proceed to Step 3 to add the actual handling and keep them green.

> Note for the implementer: the endpoint tests deliberately use non-command bodies so `HandleAsync` is never reached (no real HTTP). Behavioural coverage lives in `ReviewCommentCommandServiceTests` (Task 7). Do not add a command-body endpoint test here — it would make a live network call through the real typed `HttpClient`.

- [ ] **Step 3a: Register the responder + command service in `DependencyInjection.cs`.** In the **GitHub** branch, after the `AddHttpClient<IGitPlatform, GitHubPlatform>(...)` block (right before `services.AddSingleton<IAuthorLoginResolver>(new PassthroughAuthorLoginResolver());`), add:

```csharp
                // FP-Antwort-Kommando: eigener typed Client (gleiche Basis-Header wie die API),
                // Auth pro Request in der Impl.
                services.AddHttpClient<IReviewCommentResponder, GitHubCommentResponder>((sp, http) =>
                    ConfigureGitHubClient(http, sp.GetRequiredService<IOptions<GitHubOptions>>().Value.BaseUrl));
```

In the **GitLab** branch, after the `AddHttpClient<IAuthorLoginResolver, GitLabAuthorLoginResolver>(...)` block, add:

```csharp
                // FP-Antwort-Kommando: eigener typed Client auf denselben GitLab-Host.
                services.AddHttpClient<IReviewCommentResponder, GitLabCommentResponder>((sp, http) =>
                {
                    var opt = sp.GetRequiredService<IOptions<GitLabOptions>>().Value;
                    http.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                });
```

After the `switch` (both branches always register an `IReviewCommentResponder`), register the orchestrator once — place it just after the `switch (gitOptions.Platform) { ... }` closes:

```csharp
        // FP-Antwort-Kommando-Orchestrator (scoped — nutzt DbContext + den plattform-spezifischen Responder).
        services.AddScoped<Naudit.Infrastructure.Memory.ReviewCommentCommandService>();
```

Ensure the file has `using Naudit.Infrastructure.Git;` (the responder types) — it already `using`s the GitHub/GitLab sub-namespaces; add `using Naudit.Infrastructure.Git;` if missing.

- [ ] **Step 3b: Wire the GitHub comment branch in `Program.cs`.** In the `/webhook/github` handler, **after** `var eventType = context.Request.Headers["X-GitHub-Event"].ToString();` and **before** `var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(rawBody);`, insert:

```csharp
                // FP-Antwort-Kommando: Antwort auf einen Naudit-Inline-Kommentar. Synchron behandeln
                // (kein Review-Queue-Job), immer 200 nach der Signaturprüfung.
                if (eventType == "pull_request_review_comment")
                {
                    var commentEvent = JsonSerializer.Deserialize<GitHubReviewCommentEvent>(rawBody);
                    var reply = commentEvent is null ? null : GitHubWebhook.ToCommentReply(eventType, commentEvent);
                    if (reply is null)
                        return Results.Ok();   // kein "@naudit fp"-Kommando / keine Antwort

                    var commentGate = context.RequestServices.GetRequiredService<Naudit.Core.Abstractions.IAccessGate>();
                    if (!await commentGate.IsAllowedAsync(reply.ProjectId, context.RequestAborted))
                        return Results.Ok();

                    try
                    {
                        var handler = context.RequestServices.GetRequiredService<Naudit.Infrastructure.Memory.ReviewCommentCommandService>();
                        await handler.HandleAsync(reply, context.RequestAborted);
                    }
                    catch (Exception ex)
                    {
                        app.Logger.LogWarning(ex, "FP-Kommando-Verarbeitung (GitHub) fehlgeschlagen.");
                    }
                    return Results.Ok();
                }
```

- [ ] **Step 3c: Wire the GitLab comment branch in `Program.cs`.** Replace the GitLab handler's body from `var payload = await context.Request.ReadFromJsonAsync<GitLabWebhookPayload>();` down to (but not including) the access-gate block, so it reads:

```csharp
                var secret = gitLabOptions.Value.WebhookSecret;
                var token = context.Request.Headers["X-Gitlab-Token"].ToString();
                // Konstant-zeitlicher Vergleich wie beim /review-Endpoint — kein Timing-Leak des Secrets.
                if (!IsValidNauditToken(secret, token))
                    return Results.Unauthorized();

                // Rohen Body einmal puffern — object_kind entscheidet den Zweig (note = FP-Kommando).
                using var ms = new MemoryStream();
                await context.Request.Body.CopyToAsync(ms);
                var rawBody = ms.ToArray();
                var objectKind = JsonSerializer.Deserialize<GitLabWebhookPayload>(rawBody)?.ObjectKind;

                if (objectKind == "note")
                {
                    var noteEvent = JsonSerializer.Deserialize<GitLabNoteEvent>(rawBody);
                    var reply = noteEvent is null ? null : GitLabWebhook.ToCommentReply(noteEvent);
                    if (reply is null)
                        return Results.Ok();   // kein "@naudit fp"-Kommando / keine MR-Antwort

                    var commentGate = context.RequestServices.GetRequiredService<Naudit.Core.Abstractions.IAccessGate>();
                    if (!await commentGate.IsAllowedAsync(reply.ProjectId, context.RequestAborted))
                        return Results.Ok();

                    try
                    {
                        var handler = context.RequestServices.GetRequiredService<Naudit.Infrastructure.Memory.ReviewCommentCommandService>();
                        await handler.HandleAsync(reply, context.RequestAborted);
                    }
                    catch (Exception ex)
                    {
                        app.Logger.LogWarning(ex, "FP-Kommando-Verarbeitung (GitLab) fehlgeschlagen.");
                    }
                    return Results.Ok();
                }

                var payload = JsonSerializer.Deserialize<GitLabWebhookPayload>(rawBody);
                if (payload is null)
                    return Results.Ok();
```

(The remaining GitLab handler code — `GitLabWebhook.ToReviewRequest(payload)`, the access gate, `EnqueueAsync` — stays unchanged. Add `using Naudit.Infrastructure.Git.GitLab;` is already present; `System.Text.Json` already imported.)

- [ ] **Step 4: Build + run the affected suites**

Run: `dotnet build Naudit.slnx`
Then: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "WebhookEndpointTests|ReviewMemoryWiringTests|GitHubPlatformTests|GitLabPlatformTests"`
Expected: PASS (endpoints wired, DI resolves, nothing regressed).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/DependencyInjection.cs src/Naudit.Web/Program.cs tests/Naudit.Tests/WebhookEndpointTests.cs
git commit -m "feat(memory): Comment-Webhooks verdrahtet (GitHub review-comment / GitLab note → FP-Kommando)"
```

---

### Task 9: Setup-wizard follow-ups (subscribe to the comment events)

**Files:**
- Modify: `src/Naudit.Infrastructure/Setup/GitLabHookCreator.cs` (`note_events = true`)
- Modify: `src/Naudit.Infrastructure/Setup/GitHubManifest.cs` (`default_events` += `pull_request_review_comment`)
- Test: `tests/Naudit.Tests/GitLabHookCreatorTests.cs` (append) and `tests/Naudit.Tests/GitHubManifestTests.cs` (append)

**Interfaces:**
- Consumes: nothing new.
- Produces: GitLab hooks created with `note_events: true`; GitHub App manifest with `pull_request_review_comment` in `default_events`.

- [ ] **Step 1: Write the failing tests**

Append to `GitHubManifestTests.cs`:

```csharp
[Fact]
public void Build_subscribesToReviewCommentEvent()
{
    var manifest = GitHubManifest.Build("https://naudit.example.com", "Naudit", isPublic: false);
    Assert.Contains("pull_request", manifest.DefaultEvents);
    Assert.Contains("pull_request_review_comment", manifest.DefaultEvents);
}
```

Append to `GitLabHookCreatorTests.cs` (mirror the existing test that asserts the POST body; find the test that inspects the created-hook JSON and add an assertion, or add a focused test). A self-contained test:

```csharp
[Fact]
public async Task CreateAsync_requestsNoteEvents_forFpReplyCommand()
{
    // GET (Idempotenz-Liste) leer, dann POST 201.
    var handler = new StubHttpMessageHandler(req =>
        req.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    var creator = new GitLabHookCreator(new HttpClient(handler));

    await creator.CreateAsync("https://gitlab.example.com", "tok", "https://naudit.example.com/webhook/gitlab", "secret",
        new[] { new GitLabHookTarget(GitLabHookTargetKind.Project, "7") });

    var post = handler.Calls.Single(c => c.Method == HttpMethod.Post);
    Assert.Contains("\"note_events\":true", post.Body);
    Assert.Contains("\"merge_requests_events\":true", post.Body);   // unverändert
}
```

(Adjust `using`s to match the existing test file: `System.Net`, `System.Text`, `Naudit.Infrastructure.Setup`, `Naudit.Tests.Fakes`, `Xunit`.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "GitHubManifestTests|GitLabHookCreatorTests"`
Expected: FAIL — the new event / `note_events` are not yet emitted.

- [ ] **Step 3a: Add the GitHub event.** In `GitHubManifest.Build`, change `DefaultEvents`:

```csharp
            DefaultEvents: ["pull_request", "pull_request_review_comment"],
```

Update the adjacent XML doc comment on `Build` to mention the review-comment event alongside `pull_request`.

- [ ] **Step 3b: Add the GitLab event.** In `GitLabHookCreator.CreateOneAsync`, extend the POST body object:

```csharp
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                url = webhookUrl,
                token = secret,
                merge_requests_events = true,
                note_events = true,   // FP-Antwort-Kommando (@naudit fp) auf Inline-Kommentaren
                push_events = false,
            }), Encoding.UTF8, "application/json");
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "GitHubManifestTests|GitLabHookCreatorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Setup/GitLabHookCreator.cs src/Naudit.Infrastructure/Setup/GitHubManifest.cs tests/Naudit.Tests/GitHubManifestTests.cs tests/Naudit.Tests/GitLabHookCreatorTests.cs
git commit -m "feat(memory): Wizard abonniert Comment-Events (GitLab note_events / GitHub pull_request_review_comment)"
```

---

### Task 10: Docs + full-suite gate

**Files:**
- Modify: `docs/review-memory.md` (replace the "Outlook: PR 2b" section with the shipped behaviour)
- Modify: `CLAUDE.md` (update the comment→finding mapping paragraph: consumer now exists)

- [ ] **Step 1: Update `docs/review-memory.md`.** Replace the final `## Outlook: PR 2b` section with a shipped-feature section covering: the `@naudit fp <optional reason>` command (`fp`|`false-positive`, case-insensitive, reason = rest of the first line); the two webhook events (`pull_request_review_comment` action `created` via `X-Hub-Signature-256`; GitLab Note Hook `object_kind:note`, `noteable_type:MergeRequest` via `X-Gitlab-Token`); reply→finding mapping (GitHub `in_reply_to_id`, GitLab `discussion_id` → `PlatformCommentId`, project-scoped, ambiguous ⇒ first by id); fail-closed authorization (GitHub `author_association ∈ {OWNER, MEMBER, COLLABORATOR}`; GitLab membership `access_level ≥ 30`); the German confirmation reply; always-200-after-signature; and the wizard subscribing `note_events` / `pull_request_review_comment`. State that no config key and no migration were added.

```markdown
## Reply command: `@naudit fp` (PR 2b)

A repo member can mark a finding as a false positive **without leaving the MR/PR**:
reply to Naudit's inline comment with `@naudit fp` (or `@naudit false-positive`,
case-insensitive). The rest of that first line becomes the optional reason. The
command creates exactly the same entry as the WebUI "False positive" button
(shared `MemoryEntryWriter`, idempotent on the finding).

**Webhook events** — handled on the existing endpoints, synchronously, always
answering `200` after the signature check:

- **GitHub** `pull_request_review_comment` (action `created`), verified by the
  same `X-Hub-Signature-256` HMAC as PR/MR events. Only replies count
  (`in_reply_to_id` set); the reply's `in_reply_to_id` is the review-comment id
  Naudit stored as `PlatformCommentId`.
- **GitLab** Note Hook (`object_kind: note`, `noteable_type: MergeRequest`),
  verified by the same `X-Gitlab-Token` secret. The reply note's `discussion_id`
  is the discussion id Naudit stored as `PlatformCommentId`.

**Authorization is fail-closed** — an unverifiable author is ignored (logged,
still `200`):

- GitHub: `author_association ∈ {OWNER, MEMBER, COLLABORATOR}` (straight from the
  payload, no extra API call).
- GitLab: a members lookup (`GET …/members/all/{user_id}`) requiring
  `access_level ≥ 30` (Developer).

On success Naudit records the entry and replies in the same thread with
`"Als False Positive gemerkt."`. If the reply maps to no finding for that
project, nothing happens (logged). The **known edge** from PR 2a — two findings
on the same file+line sharing one GitHub comment id — is resolved deterministically
to the first finding by id (and logged).

**Setup wizard** subscribes to these events automatically: GitLab hooks are
created with `note_events: true`, the GitHub App manifest lists
`pull_request_review_comment` in `default_events`.

No new configuration key and no migration — the anchor columns
(`PlatformCommentId`/`PlatformNoteId`) already exist from PR 2a.
```

- [ ] **Step 2: Update `CLAUDE.md`.** In the "Comment→finding mapping" paragraph under Review memory, change the closing "No consumer yet — see `docs/review-memory.md`." to note the consumer now exists, e.g.:

```
   capture is best-effort (id lookup failure ⇒ null ids, never fails the
   already-posted review). Consumed by the `@naudit fp` reply command
   (PR 2b): GitHub `pull_request_review_comment` / GitLab Note-Hook →
   `ReviewCommentCommandService` maps the reply back to the finding via
   `PlatformCommentId` (project-scoped) and records the false positive. See
   `docs/review-memory.md`.
```

- [ ] **Step 3: Run the FULL suite (gate)**

Run: `dotnet test Naudit.slnx`
Expected: PASS — all tests green (prior 469 + the new tests). If anything red, fix before committing.

- [ ] **Step 4: Commit**

```bash
git add docs/review-memory.md CLAUDE.md
git commit -m "docs(memory): @naudit fp-Antwort-Kommando (PR 2b) dokumentiert"
```

---

## Self-Review

**1. Spec coverage** (against `docs/superpowers/specs/2026-07-09-review-memory-design.md` § "Feedback channel 2"):
- Command `@naudit fp <reason>` (`fp`|`false-positive`, case-insensitive, rest = reason) → Task 1. ✅
- Comment→finding mapping via `PlatformCommentId` (GitHub `in_reply_to_id`, GitLab `discussion_id`) → Tasks 2/3 (map key) + Task 7 (lookup). ✅
- Webhook events on existing endpoints (GitHub `pull_request_review_comment` created / GitLab Note Hook), signature as today, synchronous, always 200 → Task 8. ✅
- Bot's own + non-command replies ignored → Task 1 parser (the bot's confirmation "Als False Positive gemerkt." never parses as a command, so no loop; non-commands map to null). ✅ (Documented reasoning; no separate bot-username config needed.)
- Authorization fail-closed (GitHub `author_association`; GitLab membership ≥ Developer; unverifiable ⇒ ignore) → Tasks 4/5 + Task 7. ✅
- Confirmation reply "Als False Positive gemerkt." → Task 7 + responders. ✅
- Setup wizard follow-up (`note_events`, manifest `pull_request_review_comment`) → Task 9. ✅
- Idempotent, redaction-at-review-time (entries redacted when selected, not on write — matches WebUI FP write) → Task 6/7 reuse `MemoryEntryWriter`; no write-time redaction, consistent with PR 1. ✅
- No Core change, no migration → held throughout. ✅

**2. Placeholder scan:** every code/test step contains complete code; commands have expected outcomes. No TBD/"add error handling"/"similar to". ✅

**3. Type consistency:** `ReviewCommentReply` fields (`ProjectId`, `MergeRequestIid`, `ReplyToCommentId`, `Reason`, `AuthorLogin`, `AuthorAssociation`, `AuthorId`) are used identically in Tasks 2, 3, 4, 5, 7. `IReviewCommentResponder.IsAuthorizedAsync`/`PostReplyAsync` signatures match across Tasks 4, 5, 7. `MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, reason, createdBy, ct)` identical in Tasks 6, 7. `ReviewCommentCommandService.HandleAsync`/`ConfirmationText` consistent in Tasks 7, 8. ✅

**Known edge carried from PR 2a:** duplicate (path,line) ⇒ one GitHub comment id shared by two findings ⇒ ambiguous reply mapping — handled deterministically (first by id) and logged in Task 7; documented in Task 10.
