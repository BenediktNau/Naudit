using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Ui;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>Autor-Session-Routing: MR-Autor → aktiver Account mit Token (ohne Cooldown) →
/// per-Review-ClaudeCodeChatClient im FallbackChatClient-Gespann. Jede Nicht-Treffer-Stufe
/// fällt lautlos auf den globalen Client zurück — das Review läuft immer. Auch jede
/// unerwartete Exception bei der Auflösung selbst (DB, Resolver, Token) fällt fail-open
/// auf den globalen Client zurück, statt das ganze Review zu kippen.</summary>
public sealed class AuthorSessionRouter(
    ClaudeSessionService sessions,
    IAuthorLoginResolver authorResolver,
    SessionHealthRegistry health,
    SessionSelectionFactory selectionFactory,
    ILogger<AuthorSessionRouter> logger) : IAiClientRouter
{
    public async Task<AiClientSelection> SelectAsync(ReviewRequest request, CancellationToken ct = default)
    {
        try
        {
            var login = await authorResolver.ResolveAsync(request, ct);
            if (string.IsNullOrWhiteSpace(login))
                return selectionFactory.Global();

            var account = await sessions.FindByAuthorLoginAsync(login, ct);
            if (account is null || health.IsCoolingDown(account.Id))
                return selectionFactory.Global();

            var token = sessions.DecryptToken(account);
            if (token is null)
                return selectionFactory.Global();

            return selectionFactory.ForAccount(account.Id, token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Fail-open: eine transiente Störung (DB, Resolver, Token) darf das Review nicht kippen.
            logger.LogWarning(ex, "Autor-Session-Routing fehlgeschlagen — globaler Client.");
            return selectionFactory.Global();
        }
    }
}
