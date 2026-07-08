namespace Naudit.Web;

/// <summary>Kontrollierter In-Process-Neustart: Endpoints rufen RequestRestart, der Host stoppt,
/// die Schleife in Program.cs baut ihn neu (Config-Änderungen aus der DB werden so übernommen).</summary>
public interface IAppRestarter
{
    void RequestRestart();

    /// <summary>Merkt „Settings geändert, Neustart steht aus" — fürs Banner der Settings-Seite.</summary>
    void MarkRestartPending();

    bool RestartPending { get; }
}

public sealed class AppRestarter : IAppRestarter
{
    private volatile bool _restartRequested;
    private volatile bool _restartPending;
    private IHostApplicationLifetime? _lifetime;

    /// <summary>Nach dem Host-Bau aufrufen — vorher läuft RequestRestart ins Leere (nur Flag).</summary>
    public void Attach(IHostApplicationLifetime lifetime) => _lifetime = lifetime;

    public void RequestRestart()
    {
        _restartRequested = true;
        _lifetime?.StopApplication();
    }

    public void MarkRestartPending() => _restartPending = true;
    public bool RestartPending => _restartPending;

    /// <summary>Von der Program-Schleife nach RunAsync gelesen; setzt beide Flags zurück.</summary>
    public bool ConsumeRestartRequest()
    {
        var requested = _restartRequested;
        _restartRequested = false;
        _restartPending = false;
        return requested;
    }
}

/// <summary>Startzustand für die Settings-API: Recovery-Fehler (Config kaputt) + Loader-Warnungen
/// (z. B. nicht entschlüsselbare Secrets).</summary>
public sealed record StartupState(string? RecoveryError, IReadOnlyList<string> Warnings);
