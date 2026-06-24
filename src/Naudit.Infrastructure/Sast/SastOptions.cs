namespace Naudit.Infrastructure.Sast;

public sealed class SastOptions
{
    /// <summary>SAST/SCA-Grounding global an/aus. Aus ⇒ exakt diff-only.</summary>
    public bool Enabled { get; set; }

    /// <summary>Aktive Analyzer per Name: "semgrep", "trivy", "dotnet-sca".
    /// Leer im Objekt, damit Config-Binding eine Liste nicht verdoppelt; Standard-Fallback in DI.</summary>
    public List<string> Analyzers { get; set; } = new();

    /// <summary>Reducer-Strategie. Aktuell nur "deterministic" (Seam für späteres "llm").</summary>
    public string Reducer { get; set; } = "deterministic";

    /// <summary>Timeout je Analyzer/Tool-Aufruf.</summary>
    public TimeSpan AnalyzerTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Cap pro Category in der Verdichtung.</summary>
    public int MaxFindingsPerGroup { get; set; } = 20;
}
