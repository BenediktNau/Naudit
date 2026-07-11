using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Ui;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>Autor-Session-Routing: MR-Autor → aktiver Account mit Token (ohne Cooldown) →
/// per-Review-ClaudeCodeChatClient im FallbackChatClient-Gespann. Jede Nicht-Treffer-Stufe
/// fällt lautlos auf den globalen Client zurück — das Review läuft immer.</summary>
public sealed class AuthorSessionRouter(
    ClaudeSessionService sessions,
    IAuthorLoginResolver authorResolver,
    SessionHealthRegistry health,
    AuthorSessionsOptions options,
    AiOptions aiOptions,
    IChatClient globalClient,
    IProcessRunner runner,
    ILoggerFactory loggerFactory) : IAiClientRouter
{
    public async Task<AiClientSelection> SelectAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var login = await authorResolver.ResolveAsync(request, ct);
        if (string.IsNullOrWhiteSpace(login))
            return Global();

        var account = await sessions.FindByAuthorLoginAsync(login, ct);
        if (account is null || health.IsCoolingDown(account.Id))
            return Global();

        var token = sessions.DecryptToken(account);
        if (token is null)
            return Global();

        // Eigene AiOptions für den CLI-Lauf: Autor-Token + AuthorSessions-Modell; Timeout wie global.
        var authorClient = new ClaudeCodeChatClient(new AiOptions
        {
            Provider = AiProvider.ClaudeCode,
            Model = options.Model,
            ApiKey = token,
            TimeoutSeconds = aiOptions.TimeoutSeconds,
        }, runner);

        var accountId = account.Id;
        var fallback = new FallbackChatClient(authorClient, globalClient, accountId,
            onAuthorFailure: () => health.MarkFailure(accountId, TimeSpan.FromMinutes(options.CooldownMinutes)),
            loggerFactory.CreateLogger<FallbackChatClient>());

        return new AiClientSelection(fallback, () => fallback.AnsweredBySessionAccountId);
    }

    private AiClientSelection Global() => new(globalClient, static () => null);
}
