using System.Threading.Channels;
using Naudit.Infrastructure.Git;

namespace Naudit.Web;

/// <summary>Channel-basierte Queue für FP-Antwort-Kommandos — entkoppelt die Verarbeitung
/// (Auth + DB + Bestätigungs-Reply) von der Webhook-Delivery (enqueue, sofort 200).</summary>
public interface IFpCommandQueue
{
    ValueTask EnqueueAsync(ReviewCommentReply reply, CancellationToken ct = default);
    IAsyncEnumerable<ReviewCommentReply> DequeueAllAsync(CancellationToken ct);
}

public sealed class FpCommandQueue : IFpCommandQueue
{
    private readonly Channel<ReviewCommentReply> _channel = Channel.CreateUnbounded<ReviewCommentReply>();

    public ValueTask EnqueueAsync(ReviewCommentReply reply, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(reply, ct);

    public IAsyncEnumerable<ReviewCommentReply> DequeueAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
