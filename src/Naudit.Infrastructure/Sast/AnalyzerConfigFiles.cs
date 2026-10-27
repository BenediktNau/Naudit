namespace Naudit.Infrastructure.Sast;

/// <summary>Werkzeug-Configs unter Naudits Kontrolle. Die Analyzer laufen mit CWD = PR-Checkout,
/// und ohne expliziten Pfad greifen sie auf repo-eigene Dateien zurück (trivy.yaml, .trivyignore,
/// .gitleaks.toml/.gitleaksignore, osv-scanner.toml) — ein PR könnte damit Funde unterdrücken oder
/// (trivy: <c>output:</c>) an beliebige Pfade schreiben. Jeder Analyzer bekommt deshalb explizite
/// Pfade auf diese Dateien in einem privaten Temp-Ordner (außerhalb jedes Checkouts).
/// Fehlt eine Datei (z. B. Temp-Aufräumer bei langer Laufzeit), wird sie neu angelegt.</summary>
internal static class AnalyzerConfigFiles
{
    private static readonly Lock Gate = new();
    private static string? _dir;

    /// <summary>Leere trivy.yaml (Endung nötig: trivy/viper erkennt das Format daran).</summary>
    public static string TrivyConfig => Ensure("trivy.yaml", "");

    /// <summary>Leere Ignore-Datei für <c>--ignorefile</c>.</summary>
    public static string TrivyIgnore => Ensure("trivyignore", "");

    /// <summary>Leere osv-scanner-Config für <c>--config</c> (überschreibt die Suche je Verzeichnis).</summary>
    public static string OsvScannerConfig => Ensure("osv-scanner.toml", "");

    /// <summary>Betterleaks-/Gitleaks-Config: NUR die eingebauten Default-Regeln (eine leere Datei
    /// hieße „keine Regeln“).</summary>
    public static string BetterleaksConfig => Ensure("betterleaks.toml", "[extend]\nuseDefault = true\n");

    /// <summary>Leerer Ordner für <c>--gitleaks-ignore-path</c> (Default "." = Checkout).</summary>
    public static string EmptyDirectory
    {
        get
        {
            var dir = Path.Combine(Root(), "empty");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string Ensure(string name, string content)
    {
        var path = Path.Combine(Root(), name);
        lock (Gate)
        {
            if (!File.Exists(path) || new FileInfo(path).Length != content.Length)
                File.WriteAllText(path, content);
        }
        return path;
    }

    private static string Root()
    {
        lock (Gate)
        {
            // CreateTempSubdirectory: zufälliger Name, auf Unix Modus 0700 — kein Dritter kann
            // die Dateien vorab anlegen oder austauschen.
            if (_dir is null || !Directory.Exists(_dir))
                _dir = Directory.CreateTempSubdirectory("naudit-analyzer-config-").FullName;
            return _dir;
        }
    }
}
