using System.Threading;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>Prozess-globaler Rotationszeiger fürs Round-Robin-Routing. Bewusst in-memory
/// (kein persistenter Zustand): nach einem Neustart rotiert die Reihenfolge einfach ab 0 weiter.</summary>
public sealed class RoundRobinCursor
{
    private int _n = -1;
    /// <summary>Nächster nicht-negativer Zählwert (erste Rückgabe 0), überlaufsicher.</summary>
    public int Next() => Interlocked.Increment(ref _n) & int.MaxValue;
}
