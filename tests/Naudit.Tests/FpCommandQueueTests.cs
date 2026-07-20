using Naudit.Infrastructure.Git;
using Naudit.Web;
using Xunit;

namespace Naudit.Tests;

public class FpCommandQueueTests
{
    [Fact]
    public async Task EnqueuedReply_isDequeued()
    {
        var queue = new FpCommandQueue();
        var reply = new ReviewCommentReply("acme/widgets", 7, "555", "legacy", "bob", AuthorAssociation: "MEMBER", AuthorId: null, Command: ReviewCommandKind.FalsePositive);
        await queue.EnqueueAsync(reply);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        ReviewCommentReply? dequeued = null;
        await foreach (var item in queue.DequeueAllAsync(cts.Token))
        {
            dequeued = item;
            break;
        }

        Assert.Equal(reply, dequeued);
    }
}
