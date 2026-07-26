using Mediator;
using Microsoft.Extensions.AI;

namespace Naudit.Infrastructure.Ai.Logging;

/// <summary>Terminaler Handler der ChatCompletionRequest-Pipeline: ruft den mitgereichten
/// echten IChatClient. Die gesamte Nachvollziehbarkeit (Log + Persistenz) sitzt davor im
/// PromptLoggingBehavior — der Handler bleibt bewusst leer von Logik.</summary>
public sealed class ChatCompletionHandler : IRequestHandler<ChatCompletionRequest, ChatResponse>
{
    public async ValueTask<ChatResponse> Handle(ChatCompletionRequest request, CancellationToken cancellationToken)
        => await request.Inner.GetResponseAsync(request.Messages, request.Options, cancellationToken);
}
