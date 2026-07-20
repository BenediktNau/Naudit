namespace Naudit.Infrastructure.Ai.Sandbox;

/// <summary>Prozessweiter Sandbox-Zustand: letztes Ping-Ergebnis (null = noch nie geprüft).
/// Der Sweeper schreibt (Start + jeder Tick ⇒ Selbstheilung nach Socket-Ausfall), der Runner
/// liest (bekannt-unerreichbar ⇒ gar kein Docker-Versuch), der Status-Endpoint zeigt es an.</summary>
public sealed class SessionSandboxState
{
    private int _reachable = -1; // -1 unbekannt, 0 nein, 1 ja (Interlocked-tauglich)

    public bool? SocketReachable => _reachable switch { 1 => true, 0 => false, _ => null };

    public void ReportPing(bool ok) => Interlocked.Exchange(ref _reachable, ok ? 1 : 0);
}
