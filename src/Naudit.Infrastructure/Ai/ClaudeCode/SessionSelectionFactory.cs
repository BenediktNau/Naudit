using Mediator;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure.Ai.Logging;
using Naudit.Infrastructure.Ai.Sandbox;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>Baut die AiClientSelection für einen Session-Lauf: per-Review-ClaudeCodeChatClient
/// (Autor-/Pool-Token) im FallbackChatClient-Gespann. Von AuthorSessionRouter und
/// RoundRobinSessionRouter geteilt — eine Stelle für Client-/Fallback-/Attribution-Verdrahtung.</summary>
public sealed class SessionSelectionFactory(
    AuthorSessionsOptions options,
    AiOptions aiOptions,
    IChatClient globalClient,
    ISessionRunnerFactory runnerFactory,
    SessionHealthRegistry health,
    AiLoggingOptions logging,
    IMediator mediator,
    ILoggerFactory loggerFactory)
{
    /// <summary>Globaler Provider ohne Attribution — der Fallback-/Nicht-Treffer-Pfad.</summary>
    public AiClientSelection Global() => new(globalClient, static () => null);

    /// <summary>Session-Lauf für ein Konto mit entschlüsseltem Token.</summary>
    public AiClientSelection ForAccount(int accountId, string token)
    {
        IChatClient sessionClient = new ClaudeCodeChatClient(new AiOptions
        {
            Provider = AiProvider.ClaudeCode,
            Model = options.Model,
            ApiKey = token,
            TimeoutSeconds = aiOptions.TimeoutSeconds,
        }, runnerFactory.ForAccount(accountId));

        // Prompt-Logging um den SESSION-Client (nicht um den Fallback): so wird der Autor-/Pool-Aufruf
        // genau einmal protokolliert — schlägt er fehl, protokolliert der (bereits umhüllte) globale
        // Fallback-Client seinen Aufruf separat. Kein Doppel-Log desselben Ergebnisses.
        if (logging.Enabled)
            sessionClient = new MediatorChatClient(sessionClient, mediator);

        // Cooldown darf nie 0/negativ werden (sonst Retry-Storm gegen ein rate-limitiertes Abo).
        var cooldown = TimeSpan.FromMinutes(Math.Max(1, options.CooldownMinutes));
        var fallback = new FallbackChatClient(sessionClient, globalClient, accountId,
            onAuthorFailure: () => health.MarkFailure(accountId, cooldown),
            loggerFactory.CreateLogger<FallbackChatClient>());

        return new AiClientSelection(fallback, () => fallback.AnsweredBySessionAccountId);
    }
}
