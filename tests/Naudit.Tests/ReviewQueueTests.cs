using Naudit.Core.Models;
using Naudit.Web;
using Xunit;

namespace Naudit.Tests;

public class ReviewQueueTests
{
    [Fact]
    public async Task EnqueuedRequest_isDequeued()
    {
        var queue = new ReviewQueue();
        var request = new ReviewRequest("1", 42, "Test");
        await queue.EnqueueAsync(request);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        ReviewRequest? dequeued = null;
        await foreach (var item in queue.DequeueAllAsync(cts.Token))
        {
            dequeued = item;
            break;
        }

        Assert.Equal(request, dequeued);
    }
}
