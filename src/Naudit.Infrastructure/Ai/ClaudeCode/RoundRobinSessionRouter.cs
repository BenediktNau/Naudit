using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Ui;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>Round-Robin-Routing: rotiert die opted-in Pool-Abos (aktiv + Token) über die Reviews,
/// ignoriert den Autor. Konten auf Cooldown und undekryptierbare Token werden übersprungen;
/// ist kein Kandidat nutzbar, fällt es lautlos auf den globalen Client zurück.</summary>
public sealed class RoundRobinSessionRouter(
    ClaudeSessionService sessions,
    SessionHealthRegistry health,
    RoundRobinCursor cursor,
    AuthorSessionsOptions options,
    AiOptions aiOptions,
    IChatClient globalClient,
    IProcessRunner runner,
    ILoggerFactory loggerFactory) : IAiClientRouter
{
    public async Task<AiClientSelection> SelectAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var pool = await sessions.GetPoolCandidatesAsync(ct);
        // Cooldown-Konten raus; Id-Reihenfolge aus der Query bleibt erhalten.
        var eligible = pool.Where(a => !health.IsCoolingDown(a.Id)).ToList();
        if (eligible.Count == 0)
            return Global();

        // Ein Cursor-Schritt pro Review; ab dort das erste Konto mit entschlüsselbarem Token nehmen
        // (undekryptierbare überspringen, nicht global fallen).
        var start = cursor.Next() % eligible.Count;
        for (var i = 0; i < eligible.Count; i++)
        {
            var account = eligible[(start + i) % eligible.Count];
            var token = sessions.DecryptToken(account);
            if (token is null)
                continue;

            var poolClient = new ClaudeCodeChatClient(new AiOptions
            {
                Provider = AiProvider.ClaudeCode,
                Model = options.Model,
                ApiKey = token,
                TimeoutSeconds = aiOptions.TimeoutSeconds,
            }, runner);

            var accountId = account.Id;
            var fallback = new FallbackChatClient(poolClient, globalClient, accountId,
                onAuthorFailure: () => health.MarkFailure(accountId, TimeSpan.FromMinutes(options.CooldownMinutes)),
                loggerFactory.CreateLogger<FallbackChatClient>());

            return new AiClientSelection(fallback, () => fallback.AnsweredBySessionAccountId);
        }

        return Global(); // alle Kandidaten-Token undekryptierbar
    }

    private AiClientSelection Global() => new(globalClient, static () => null);
}
