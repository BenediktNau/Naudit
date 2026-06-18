using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ReviewServiceTests
{
    private static readonly ReviewRequest Request = new("1", 42, "Title");

    [Fact]
    public async Task ReviewAsync_postsSummary_andReturnsApprove()
    {
        var chat = new FakeChatClient("""{"summary":"## Review\n- looks fine","verdict":"approve"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        var result = await service.ReviewAsync(Request);

        Assert.Equal("## Review\n- looks fine", git.PostedMarkdown);
        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
        Assert.Equal("SYS", chat.LastMessages![0].Text);
    }

    [Fact]
    public async Task ReviewAsync_returnsRequestChanges_whenModelSaysSo()
    {
        var chat = new FakeChatClient("""{"summary":"## Review\n- bug here","verdict":"request_changes"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.RequestChanges, result.Verdict);
        Assert.Equal("## Review\n- bug here", git.PostedMarkdown);
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
    public async Task ReviewAsync_withUnknownVerdict_throws()
    {
        // Fail-closed: ein unbekanntes/kaputtes Verdict darf das Gate nicht still auf approve fallen lassen.
        var chat = new FakeChatClient("""{"summary":"## Review\n- ?","verdict":"maybe"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync(Request));
    }
}
