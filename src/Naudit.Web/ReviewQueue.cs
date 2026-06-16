using System.Threading.Channels;
using Naudit.Core.Models;

namespace Naudit.Web;

public interface IReviewQueue
{
    ValueTask EnqueueAsync(ReviewRequest request, CancellationToken ct = default);
    IAsyncEnumerable<ReviewRequest> DequeueAllAsync(CancellationToken ct);
}

public sealed class ReviewQueue : IReviewQueue
{
    private readonly Channel<ReviewRequest> _channel = Channel.CreateUnbounded<ReviewRequest>();

    public ValueTask EnqueueAsync(ReviewRequest request, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(request, ct);

    public IAsyncEnumerable<ReviewRequest> DequeueAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
