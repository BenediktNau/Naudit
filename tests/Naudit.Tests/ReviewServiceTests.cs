using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ReviewServiceTests
{
    private static readonly ReviewRequest Request = new("1", 42, "Title");

    [Fact]
    public async Task ReviewAsync_postsModelOutput_asSummary()
    {
        var chat = new FakeChatClient("## Review\n- looks fine");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await service.ReviewAsync(Request);

        Assert.Equal("## Review\n- looks fine", git.PostedMarkdown);
        Assert.Equal("SYS", chat.LastMessages![0].Text);
    }

    [Fact]
    public async Task ReviewAsync_withNoChanges_postsNothing()
    {
        var chat = new FakeChatClient("unused");
        var git = new FakeGitPlatform([]);
        var service = new ReviewService(chat, git, new ReviewOptions());

        await service.ReviewAsync(Request);

        Assert.Equal(0, git.PostCallCount);
    }
}
