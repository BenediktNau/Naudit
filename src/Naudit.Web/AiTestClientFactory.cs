using Microsoft.Extensions.AI;
using Naudit.Infrastructure.Ai;

namespace Naudit.Web;

/// <summary>Seam fuer den AI-Verbindungstest: Produktion = AiClientFactory.Create,
/// Tests ersetzen die Funktion per DI (kein Netz — Testansatz des Repos).</summary>
public sealed record AiTestClientFactory(Func<AiOptions, IChatClient> Create);
