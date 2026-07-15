using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Ui;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>Round-Robin-Routing: rotiert die opted-in Pool-Abos (aktiv + Token) über die Reviews,
/// ignoriert den Autor. Konten auf Cooldown und undekryptierbare Token werden übersprungen;
/// ist kein Kandidat nutzbar, fällt es lautlos auf den globalen Client zurück. Auch jede
/// unerwartete Exception bei der Auflösung selbst (DB, Token) fällt fail-open auf den
/// globalen Client zurück, statt das ganze Review zu kippen.</summary>
public sealed class RoundRobinSessionRouter(
    ClaudeSessionService sessions,
    SessionHealthRegistry health,
    RoundRobinCursor cursor,
    SessionSelectionFactory selectionFactory,
    ILogger<RoundRobinSessionRouter> logger) : IAiClientRouter
{
    public async Task<AiClientSelection> SelectAsync(ReviewRequest request, CancellationToken ct = default)
    {
        try
        {
            var pool = await sessions.GetPoolCandidatesAsync(ct);
            // Cooldown-Konten raus; Id-Reihenfolge aus der Query bleibt erhalten.
            var eligible = pool.Where(a => !health.IsCoolingDown(a.Id)).ToList();
            if (eligible.Count == 0)
                return selectionFactory.Global();

            // Ein Cursor-Schritt pro Review; ab dort das erste Konto mit entschlüsselbarem Token.
            var start = cursor.Next() % eligible.Count;
            for (var i = 0; i < eligible.Count; i++)
            {
                var account = eligible[(start + i) % eligible.Count];
                var token = sessions.DecryptToken(account);
                if (token is null)
                    continue;   // undekryptierbar ⇒ nächstes Pool-Abo
                return selectionFactory.ForAccount(account.Id, token);
            }
            return selectionFactory.Global(); // alle Kandidaten-Token undekryptierbar
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Fail-open: DB-/Pool-Fehler dürfen das Review nicht kippen.
            logger.LogWarning(ex, "Round-Robin-Session-Routing fehlgeschlagen — globaler Client.");
            return selectionFactory.Global();
        }
    }
}
