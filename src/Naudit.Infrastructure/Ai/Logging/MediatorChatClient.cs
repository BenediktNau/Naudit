using Mediator;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Naudit.Infrastructure.Ai.Logging;

/// <summary>IChatClient-Decorator, der jeden GetResponseAsync-Aufruf durch den Mediator schickt,
/// damit das PromptLoggingBehavior als Pipeline-Middleware greift. Core (ReviewService) sieht
/// weiterhin nur IChatClient — der Mediator bleibt vollständig eine Infrastructure-Sache.
/// Streaming/GetService/Dispose reichen unverändert an den inneren Client durch (der Review-Pfad
/// nutzt kein Streaming).</summary>
public sealed class MediatorChatClient(IChatClient inner, IMediator mediator, ILogger<MediatorChatClient>? logger = null) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        return await mediator.Send(new ChatCompletionRequest(inner, list, options), cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Brotkrume statt stiller Lücke: der Review-Pfad streamt nicht, aber ein künftiger
        // streamender Aufrufer bekäme hier sonst unbemerkt gar kein Protokoll.
        logger?.LogDebug("Streaming-Aufruf umgeht das Prompt-Logging (kein Transcript) — siehe docs/prompt-logging.md.");
        return inner.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();
}
