using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>Autor-Session mit globalem Fallback: scheitert der Autor-Lauf (JEDE Exception —
/// bewusst keine stderr-Klassifikation), meldet onAuthorFailure den Cooldown und der globale
/// Client läuft genau einmal mit denselben Messages. Scheitert auch der ⇒ fail-closed wie heute.
/// AnsweredBySessionAccountId NACH dem Aufruf lesen (pro Review eine eigene Instanz).</summary>
public sealed class FallbackChatClient(
    IChatClient author, IChatClient global, int sessionAccountId, Action onAuthorFailure, ILogger logger) : IChatClient
{
    public int? AnsweredBySessionAccountId { get; private set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList(); // zweifach enumerierbar für den Fallback-Lauf
        try
        {
            var response = await author.GetResponseAsync(list, options, cancellationToken);
            AnsweredBySessionAccountId = sessionAccountId;
            return response;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            onAuthorFailure();
            logger.LogWarning(ex, "Autor-Session (Account {AccountId}) fehlgeschlagen — Fallback auf den globalen Provider.", sessionAccountId);
            AnsweredBySessionAccountId = null;
            return await global.GetResponseAsync(list, options, cancellationToken);
        }
    }

    // ReviewService nutzt nur die non-streaming Variante; dünner Wrapper wie im ClaudeCodeChatClient.
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
