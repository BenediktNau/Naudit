using Microsoft.Extensions.AI;
using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Wählt pro Review den Chat-Client (z. B. Autor-Session statt globalem Provider).
/// UsedSessionAccountId erst NACH dem Chat-Aufruf auswerten: bei einem Fallback-Gespann steht
/// erst dann fest, welcher Pfad tatsächlich geantwortet hat.</summary>
public interface IAiClientRouter
{
    Task<AiClientSelection> SelectAsync(ReviewRequest request, CancellationToken ct = default);
}

/// <summary>Routing-Ergebnis: der zu nutzende Client plus nachträgliche Attribution
/// (Account-Id der Autor-Session oder null = globaler Provider).</summary>
public sealed record AiClientSelection(IChatClient Client, Func<int?> UsedSessionAccountId);

/// <summary>Default ohne Autor-Sessions: immer derselbe (globale) Client — heutiges Verhalten.</summary>
public sealed class SingleClientRouter(IChatClient client) : IAiClientRouter
{
    public Task<AiClientSelection> SelectAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult(new AiClientSelection(client, static () => null));
}
