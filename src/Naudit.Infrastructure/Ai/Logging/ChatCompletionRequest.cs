using Mediator;
using Microsoft.Extensions.AI;

namespace Naudit.Infrastructure.Ai.Logging;

/// <summary>Mediator-Message für einen einzelnen LLM-Aufruf. Trägt den TARGET-Client selbst mit
/// (Inner), weil pro Review verschiedene Clients gewählt werden (global, Autor-Session, Pool) —
/// der Handler kann ihn nicht statisch aus DI kennen. So bleibt der Handler ein dünner Durchreicher
/// und das PromptLoggingBehavior legt sich als Pipeline-Middleware genau um diesen einen Aufruf.</summary>
public sealed record ChatCompletionRequest(
    IChatClient Inner,
    IList<ChatMessage> Messages,
    ChatOptions? Options) : IRequest<ChatResponse>;
