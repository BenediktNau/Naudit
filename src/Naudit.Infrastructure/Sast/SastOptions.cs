namespace Naudit.Infrastructure.Sast;

public sealed class SastOptions
{
    /// <summary>SAST/SCA-Grounding global an/aus. Aus ⇒ exakt diff-only.</summary>
    public bool Enabled { get; set; }

    /// <summary>Aktive Analyzer per Name: "opengrep", "trivy", "dotnet-sca".
    /// Leer im Objekt, damit Config-Binding eine Liste nicht verdoppelt; Standard-Fallback in DI.</summary>
    public List<string> Analyzers { get; set; } = new();

    /// <summary>Zusätzliche Regelquellen für OpenGrep (je Eintrag ein `--config`-Pfad). Additiv: die
    /// Defaults (<see cref="DefaultOpengrepRules"/>) laufen IMMER mit (siehe <see cref="ResolveOpengrepRules"/>).
    /// Bewusst kein "auto": das zöge die lizenzbelasteten Registry-Regeln + Telemetrie.</summary>
    public List<string> OpengrepRules { get; set; } = new();

    /// <summary>Standard-Regelquellen im Image: der <b>volle</b> gepinnte opengrep-rules-Baum
    /// (alle Sprachen — der Build entfernt vorher die Nicht-Regel-YAMLs, die sonst den Scan
    /// abbrächen) plus Naudits eigenes .NET-Security-Overlay.</summary>
    public static readonly IReadOnlyList<string> DefaultOpengrepRules =
        ["/opt/opengrep-rules", "/opt/naudit-rules"];

    /// <summary>Effektive `--config`-Pfade: Defaults laufen IMMER (Overlay kann nie versehentlich
    /// wegfallen), konfigurierte Pfade kommen additiv dazu, Duplikate dedupliziert.</summary>
    public static List<string> ResolveOpengrepRules(IEnumerable<string> configured)
        => DefaultOpengrepRules.Concat(configured).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Reducer-Strategie. Aktuell nur "deterministic" (Seam für späteres "llm").</summary>
    public string Reducer { get; set; } = "deterministic";

    /// <summary>Timeout je Analyzer/Tool-Aufruf.</summary>
    public TimeSpan AnalyzerTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Cap pro Category in der Verdichtung.</summary>
    public int MaxFindingsPerGroup { get; set; } = 20;
}
