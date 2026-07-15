using Microsoft.Extensions.AI;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Infrastructure.Redaction;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ReviewAuditSinkTests
{
    private static ReviewService CreateService(IChatClient chat, IGitPlatform git, IReviewAuditSink sink) =>
        new(chat, git, new ReviewOptions { SystemPrompt = "s" },
            new FakeWorkspaceProvider(), [], new FakeFindingReducer(),
            new NullPromptRedactor(), new FakeContextCollector(), sink,
            new FakeRoundtripCounter());

    [Fact]
    public async Task ReviewAsync_recordsAudit_withVerdictAndFindings()
    {
        var chat = new FakeChatClient(
            """{"summary":"ok","comments":[{"file":"a.cs","line":1,"comment":"sqli","severity":"high","confidence":"high"}]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+var x = 1;")]);
        var sink = new FakeReviewAuditSink();

        await CreateService(chat, git, sink).ReviewAsync(new ReviewRequest("owner/repo", 7, "T"));

        var audit = Assert.Single(sink.Recorded);
        Assert.Equal("owner/repo", audit.ProjectId);
        Assert.Equal(7, audit.MergeRequestIid);
        Assert.Equal(ReviewVerdict.RequestChanges, audit.Verdict);
        Assert.Single(audit.Findings);
        Assert.Equal(FindingSeverity.High, audit.Findings[0].Severity);
    }

    [Fact]
    public async Task ReviewAsync_sinkFailure_doesNotFailReview()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+var x = 1;")]);
        var sink = new FakeReviewAuditSink { ThrowOnRecord = true };

        var result = await CreateService(chat, git, sink).ReviewAsync(new ReviewRequest("o/r", 1, "T"));

        Assert.Equal(ReviewVerdict.Approve, result.Verdict); // Review lief trotz Sink-Fehler durch
    }

    [Fact]
    public async Task ReviewAsync_noChanges_recordsNothing()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([]);
        var sink = new FakeReviewAuditSink();

        await CreateService(chat, git, sink).ReviewAsync(new ReviewRequest("o/r", 1, "T"));

        Assert.Empty(sink.Recorded);
    }
}
