using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ReviewServiceTests
{
    private static readonly ReviewRequest Request = new("1", 42, "Title");

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

    [Fact]
    public async Task ReviewAsync_findingWithBlankBody_isNotPosted()
    {
        // Robustheit: ein leerer comment-Body würde beim Plattform-POST scheitern -> verwerfen.
        var chat = new FakeChatClient(
            """{"verdict":"request_changes","summary":"## R","comments":[{"file":"src/Foo.cs","line":1,"comment":"   "}]}""");
        var git = new FakeGitPlatform([new CodeChange("src/Foo.cs", "@@ -0,0 +1,1 @@\n+var x = foo();")]);
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await service.ReviewAsync(Request);

        Assert.Empty(git.PostedComments);
        Assert.Contains("0 inline, 0 ohne Position", git.PostedMarkdown!);
    }

    [Fact]
    public async Task ReviewAsync_withNoChanges_postsNothing_andApproves()
    {
        var chat = new FakeChatClient("unused");
        var git = new FakeGitPlatform([]);
        var service = new ReviewService(chat, git, new ReviewOptions());

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
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

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
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await service.ReviewAsync(Request);

        Assert.Empty(git.PostedComments);
        Assert.Contains("no-file finding", git.PostedMarkdown!);
        Assert.Contains("ohne Position", git.PostedMarkdown!);
    }

    [Fact]
    public async Task ReviewAsync_withUnknownVerdict_throws()
    {
        // Fail-closed: ein unbekanntes/kaputtes Verdict darf das Gate nicht still auf approve fallen lassen.
        var chat = new FakeChatClient("""{"summary":"## Review\n- ?","verdict":"maybe"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync(Request));
    }
}
