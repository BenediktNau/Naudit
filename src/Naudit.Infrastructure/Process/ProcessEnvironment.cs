namespace Naudit.Infrastructure.Process;

/// <summary>Allowlists für <see cref="ProcessSpec.InheritedEnvironment"/>: was ein Werkzeug über
/// fremdem Code (git, SAST/SCA-Analyzer) von der Host-Umgebung erben darf.</summary>
public static class ProcessEnvironment
{
    /// <summary>Basis jedes minimal gestarteten Prozesses: Suchpfad, Home/Temp, Locale/Zeitzone,
    /// Proxy- und CA-Einstellungen (ohne die scheitert ein Fetch/DB-Download hinter einem
    /// Firmen-Proxy) sowie die Windows-Pflichtvariablen. Bewusst KEIN Naudit__*.</summary>
    public static readonly IReadOnlyList<string> Minimal =
    [
        "PATH", "HOME", "USER", "LOGNAME", "TMPDIR", "TMP", "TEMP",
        "LANG", "LANGUAGE", "LC_*", "TZ", "XDG_*",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY",
        "SSL_CERT_FILE", "SSL_CERT_DIR",
        // Windows: ohne diese starten viele Binaries gar nicht.
        "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR", "COMSPEC", "PATHEXT", "USERPROFILE", "APPDATA", "LOCALAPPDATA",
    ];

    /// <summary>Minimal-Env plus werkzeugspezifische Einträge (z. B. "TRIVY_*").</summary>
    public static IReadOnlyList<string> MinimalPlus(params string[] extra) => [.. Minimal, .. extra];

    /// <summary>Prüft, ob <paramref name="key"/> von der Allowlist erfasst ist (exakt oder Präfix-*).</summary>
    public static bool IsAllowed(string key, IReadOnlyList<string> allowlist)
    {
        foreach (var entry in allowlist)
        {
            if (entry.EndsWith('*'))
            {
                if (key.StartsWith(entry[..^1], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (key.Equals(entry, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
