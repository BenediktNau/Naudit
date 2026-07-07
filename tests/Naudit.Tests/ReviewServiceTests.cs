using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ReviewServiceTests
{
    private static readonly ReviewRequest Request = new("1", 42, "Title");

    private static ReviewService CreateService(
        Microsoft.Extensions.AI.IChatClient chat,
        Naudit.Core.Abstractions.IGitPlatform git,
        ReviewOptions options,
        IEnumerable<ISastAnalyzer>? analyzers = null,
        FakeWorkspaceProvider? workspace = null,
        IPromptRedactor? redactor = null,
        IContextCollector? contextCollector = null)
        => new(chat, git, options,
            workspace ?? new FakeWorkspaceProvider(),
            analyzers ?? Array.Empty<ISastAnalyzer>(),
            new FakeFindingReducer(),
            redactor ?? new NullPromptRedactor(),
            contextCollector ?? new FakeContextCollector());

    [Fact]
    public async Task ReviewAsync_postsComposedSummary_andReturnsApprove()
    {
        var chat = new FakeChatClient("""{"summary":"## Review\n- looks fine","verdict":"approve","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        var result = await service.ReviewAsync(Request);

        Assert.Contains("looks fine", git.PostedMarkdown!);
        Assert.Contains("approve", git.PostedMarkdown!);
        Assert.Empty(git.PostedComments);
        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
        Assert.Equal("SYS", chat.LastMessages![0].Text);
    }

    [Fact]
    public async Task ReviewAsync_blocks_onConfirmedHighFinding()
    {
        // Severity-bewusstes Gate: ein bestätigter High-Fund (High-Confidence) ⇒ request_changes.
        var chat = new FakeChatClient(
            """{"summary":"## Review\n- bug here","comments":[{"file":"a.cs","line":1,"comment":"bug here","severity":"high","confidence":"high"}]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.RequestChanges, result.Verdict);
        Assert.Contains("bug here", git.PostedMarkdown!);
    }

    [Fact]
    public async Task ReviewAsync_validLine_isPostedInline_withSeverityBadgeAndFields()
    {
        var chat = new FakeChatClient(
            """{"summary":"## Review","comments":[{"file":"src/Foo.cs","line":1,"comment":"null deref","severity":"medium","confidence":"high"}]}""");
        var git = new FakeGitPlatform([new CodeChange("src/Foo.cs", "@@ -0,0 +1,1 @@\n+var x = foo();")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await service.ReviewAsync(Request);

        var inline = Assert.Single(git.PostedComments);
        Assert.Equal("src/Foo.cs", inline.FilePath);
        Assert.Equal(1, inline.NewLine);
        Assert.Contains("null deref", inline.Body);     // Originaltext bleibt erhalten
        Assert.Contains("Medium", inline.Body);          // sichtbare Severity-Plakette am Kommentar
        Assert.Contains("confidence high", inline.Body);
        Assert.Equal(FindingSeverity.Medium, inline.Severity);   // strukturiert für das Gate
        Assert.Equal(ReviewConfidence.High, inline.Confidence);
    }

    [Fact]
    public async Task ReviewAsync_invalidLine_goesToSummary_notInline()
    {
        var chat = new FakeChatClient(
            """{"summary":"## Review","verdict":"request_changes","comments":[{"file":"src/Foo.cs","line":99,"comment":"orphan finding"}]}""");
        var git = new FakeGitPlatform([new CodeChange("src/Foo.cs", "@@ -0,0 +1,1 @@\n+only one line")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await service.ReviewAsync(Request);

        Assert.Empty(git.PostedComments);
        Assert.Contains("orphan finding", git.PostedMarkdown!);
        Assert.Contains("ohne Position", git.PostedMarkdown!);
    }

    [Fact]
    public async Task ReviewAsync_findingWithBlankBody_isNotPosted()
    {
        // Robustheit: ein leerer comment-Body würde beim Plattform-POST scheitern -> verwerfen.
        var chat = new FakeChatClient(
            """{"verdict":"request_changes","summary":"## R","comments":[{"file":"src/Foo.cs","line":1,"comment":"   "}]}""");
        var git = new FakeGitPlatform([new CodeChange("src/Foo.cs", "@@ -0,0 +1,1 @@\n+var x = foo();")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await service.ReviewAsync(Request);

        Assert.Empty(git.PostedComments);
        Assert.Contains("0 inline, 0 ohne Position", git.PostedMarkdown!);
    }

    [Fact]
    public async Task ReviewAsync_withNoChanges_postsNothing_andApproves()
    {
        var chat = new FakeChatClient("unused");
        var git = new FakeGitPlatform([]);
        var service = CreateService(chat, git, new ReviewOptions());

        var result = await service.ReviewAsync(Request);

        Assert.Equal(0, git.PostCallCount);
        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
    }

    [Fact]
    public async Task ReviewAsync_withNullSummary_doesNotThrow_andStillPosts()
    {
        // Robustheit: fehlender summary-Key darf den Review nicht mit NRE abbrechen.
        var chat = new FakeChatClient("""{"verdict":"approve","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await service.ReviewAsync(Request);

        Assert.Equal(1, git.PostCallCount);
        Assert.Contains("approve", git.PostedMarkdown!);
    }

    [Fact]
    public async Task ReviewAsync_findingWithNullFile_goesToSummary_notInline()
    {
        // Robustheit: Finding ohne file-Key darf nicht mit ArgumentNullException abbrechen.
        var chat = new FakeChatClient(
            """{"verdict":"request_changes","summary":"## R","comments":[{"line":1,"comment":"no-file finding"}]}""");
        var git = new FakeGitPlatform([new CodeChange("src/Foo.cs", "@@ -0,0 +1,1 @@\n+only one line")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await service.ReviewAsync(Request);

        Assert.Empty(git.PostedComments);
        Assert.Contains("no-file finding", git.PostedMarkdown!);
        Assert.Contains("ohne Position", git.PostedMarkdown!);
    }

    [Fact]
    public async Task ReviewAsync_unparseableResponse_throws()
    {
        // Fail-closed bleibt für "gar keine parsebare Antwort": darf nicht still auf approve fallen.
        var chat = new FakeChatClient("null");   // JSON-Literal null ⇒ Deserialize liefert null
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync(Request));
    }

    [Fact]
    public async Task ReviewAsync_doesNotBlock_onHighSeverity_butLowConfidence()
    {
        // BA-Empfehlung #5: valider Code soll nicht an einem schwachen (Low-Confidence) Signal scheitern.
        var chat = new FakeChatClient(
            """{"summary":"s","comments":[{"file":"a.cs","line":1,"comment":"maybe?","severity":"high","confidence":"low"}]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
    }

    [Fact]
    public async Task ReviewAsync_doesNotBlock_onMediumSeverity_belowDefaultThreshold()
    {
        var chat = new FakeChatClient(
            """{"summary":"s","comments":[{"file":"a.cs","line":1,"comment":"nit","severity":"medium","confidence":"high"}]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
    }

    [Fact]
    public async Task ReviewAsync_commentWithoutSeverity_doesNotBlock()
    {
        // Fehlende severity/confidence ⇒ Info/Low ⇒ nicht blockierend.
        var chat = new FakeChatClient(
            """{"summary":"s","comments":[{"file":"a.cs","line":1,"comment":"style"}]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
    }

    [Fact]
    public async Task ReviewAsync_gateThreshold_isConfigurable()
    {
        // MinSeverity auf Critical hochgesetzt ⇒ ein High-Fund blockt nicht mehr.
        var chat = new FakeChatClient(
            """{"summary":"s","comments":[{"file":"a.cs","line":1,"comment":"bug","severity":"high","confidence":"high"}]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var options = new ReviewOptions { SystemPrompt = "SYS" };
        options.Gate.MinSeverity = FindingSeverity.Critical;
        var service = CreateService(chat, git, options);

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
    }

    [Fact]
    public async Task ReviewAsync_orphanHighFinding_stillBlocks_andLandsInSummary()
    {
        // Auch nicht-verortbare Funde tragen Severity/Confidence und treiben das Gate.
        var chat = new FakeChatClient(
            """{"summary":"s","comments":[{"file":"a.cs","line":99,"comment":"race condition","severity":"critical","confidence":"high"}]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.RequestChanges, result.Verdict);
        Assert.Empty(git.PostedComments);
        Assert.Contains("race condition", git.PostedMarkdown!);
        Assert.Contains("ohne Position", git.PostedMarkdown!);
    }

    [Fact]
    public async Task ReviewAsync_orphanFindingWithoutLine_stillBlocks_andLandsInSummary()
    {
        // Neuer Kontrakt: ein Fund OHNE "line" (nicht-verortbar) bleibt strukturiert in comments[],
        // trägt Severity/Confidence und treibt das Gate — statt unstrukturiert in summary zu landen.
        var chat = new FakeChatClient(
            """{"summary":"s","comments":[{"file":"a.cs","comment":"global race","severity":"critical","confidence":"high"}]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.RequestChanges, result.Verdict);
        Assert.Empty(git.PostedComments);
        Assert.Contains("global race", git.PostedMarkdown!);
        Assert.Contains("ohne Position", git.PostedMarkdown!);
    }

    [Fact]
    public async Task ReviewAsync_groundsFindings_inPrompt_andAnnotatesInDiff()
    {
        var chat = new FakeChatClient("""{"summary":"ok","verdict":"approve"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var finding = new ScanFinding("opengrep", FindingCategory.Sast, FindingSeverity.High, "sqli", "rule.sqli", "a.cs", 5);
        var analyzers = new[] { new FakeSastAnalyzer("opengrep", new[] { finding }) };
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" }, analyzers);

        await service.ReviewAsync(Request);

        var userText = chat.LastMessages![1].Text!;
        Assert.Contains("[HIGH][in diff] opengrep", userText);
        Assert.Contains("a.cs:5", userText);
    }

    [Fact]
    public async Task ReviewAsync_marksFindingPreExisting_whenFileNotInDiff()
    {
        var chat = new FakeChatClient("""{"summary":"ok","verdict":"approve"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var finding = new ScanFinding("opengrep", FindingCategory.Sast, FindingSeverity.Low, "x", "r", "other.cs", 1);
        var analyzers = new[] { new FakeSastAnalyzer("opengrep", new[] { finding }) };
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" }, analyzers);

        await service.ReviewAsync(Request);

        Assert.Contains("[LOW][pre-existing] opengrep", chat.LastMessages![1].Text!);
    }

    [Fact]
    public async Task ReviewAsync_oneAnalyzerThrows_stillReviewsWithOthers()
    {
        var chat = new FakeChatClient("""{"summary":"ok","verdict":"approve"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var good = new ScanFinding("trivy", FindingCategory.Sca, FindingSeverity.Critical, "CVE", "CVE-1", "lock");
        var analyzers = new ISastAnalyzer[]
        {
            new FakeSastAnalyzer("boom", Array.Empty<ScanFinding>(), throws: true),
            new FakeSastAnalyzer("trivy", new[] { good }),
        };
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" }, analyzers);

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
        Assert.Contains("CVE-1", chat.LastMessages![1].Text!);
    }

    [Fact]
    public async Task ReviewAsync_checkoutFails_degradesToDiffOnly()
    {
        var chat = new FakeChatClient("""{"summary":"ok","verdict":"approve"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var analyzers = new[] { new FakeSastAnalyzer("opengrep", Array.Empty<ScanFinding>()) };
        var ws = new FakeWorkspaceProvider { ThrowOnCheckout = true };
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" }, analyzers, ws);

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
        Assert.Contains("No tool findings.", chat.LastMessages![1].Text!);
        Assert.Equal(1, git.PostCallCount);
    }

    [Fact]
    public async Task ReviewAsync_redactsDiff_findingMessage_andTitle_beforePrompt()
    {
        // Beweist: Diff, Finding-Message UND Titel laufen vor dem Prompt-Aufbau durch den Redactor.
        var chat = new FakeChatClient("""{"summary":"ok","verdict":"approve"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@\n+var k = \"SECRET\";")]);
        var finding = new ScanFinding("trivy", FindingCategory.Sca, FindingSeverity.High, "leak SECRET here", "R", "a.cs", 1);
        var analyzers = new[] { new FakeSastAnalyzer("trivy", new[] { finding }) };
        var redactor = new FakePromptRedactor("SECRET");
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
            analyzers, redactor: redactor);

        await service.ReviewAsync(new ReviewRequest("1", 42, "Fix SECRET in config"));

        var userText = chat.LastMessages![1].Text!;
        Assert.DoesNotContain("SECRET", userText);   // Diff + Finding-Message + Titel redigiert
        Assert.Contains("«red»", userText);
        Assert.Equal(3, redactor.Calls);             // pro Feld einzeln redigiert (Diff, Finding, Titel)
    }

    [Fact]
    public async Task ReviewAsync_noAnalyzers_andContextDisabled_doesNotCheckout()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var ws = new FakeWorkspaceProvider();
        var options = new ReviewOptions();
        options.Context.Enabled = false;
        var service = CreateService(chat, git, options, analyzers: null, workspace: ws);

        await service.ReviewAsync(Request);

        Assert.False(ws.CheckoutCalled);
    }

    [Fact]
    public async Task ReviewAsync_contextEnabled_noAnalyzers_checksOut_andCollects()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var ws = new FakeWorkspaceProvider();
        var collector = new FakeContextCollector();   // Context.Enabled default true
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
            analyzers: null, workspace: ws, contextCollector: collector);

        await service.ReviewAsync(Request);

        Assert.True(ws.CheckoutCalled);
        Assert.True(collector.Called);
    }

    [Fact]
    public async Task ReviewAsync_groundsContext_inPrompt()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var ctx = new ReviewContext(
            new[] { new FileEnvironment("src/Foo.cs", 1, "class Foo { }", IsFullFile: true) },
            Array.Empty<SymbolUsage>(), null);
        var collector = new FakeContextCollector(ctx);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
            contextCollector: collector);

        await service.ReviewAsync(Request);

        var userText = chat.LastMessages![1].Text!;
        Assert.Contains("# Repository context", userText);
        Assert.Contains("class Foo { }", userText);
    }

    [Fact]
    public async Task ReviewAsync_redactsContext_beforePrompt()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var ctx = new ReviewContext(
            new[] { new FileEnvironment("src/Foo.cs", 1, "var k = \"SECRET\";", IsFullFile: true) },
            Array.Empty<SymbolUsage>(), null);
        var redactor = new FakePromptRedactor("SECRET");
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
            redactor: redactor, contextCollector: new FakeContextCollector(ctx));

        await service.ReviewAsync(Request);

        var userText = chat.LastMessages![1].Text!;
        Assert.DoesNotContain("SECRET", userText);   // Kontext läuft durch den Redactor
        Assert.Contains("«red»", userText);
    }

    [Fact]
    public async Task ReviewAsync_contextCollectorThrows_stillReviews_diffOnly()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var collector = new FakeContextCollector(throws: true);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
            contextCollector: collector);

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
        Assert.Equal(1, git.PostCallCount);
        Assert.DoesNotContain("# Repository context", chat.LastMessages![1].Text!);
    }
}
