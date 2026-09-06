namespace Naudit.Core.Models;

/// <summary>Art des Funds: statische Code-Analyse, Dependency-/SCA-Scan oder Secrets-Detection.</summary>
public enum FindingCategory { Sast, Sca, Secrets }

/// <summary>Normalisierter Schweregrad über alle Tools hinweg (Reihenfolge = Rang für Sortierung).</summary>
public enum FindingSeverity { Info, Low, Medium, High, Critical }

/// <summary>Ein tool-agnostischer, normalisierter Fund (OpenGrep/Trivy/dotnet/…).</summary>
public sealed record ScanFinding(
    string Tool,
    FindingCategory Category,
    FindingSeverity Severity,
    string Message,
    string? RuleId = null,
    string? FilePath = null,
    int? Line = null)
{
    /// <summary>Vom Orchestrator gesetzt: liegt der Fund auf einer kommentierbaren Diff-Zeile
    /// (hinzugefügt oder Kontext im Hunk)? Nur solche Funde kann das Modell verankern.</summary>
    public bool InDiff { get; init; }

    /// <summary>Vom Orchestrator gesetzt: liegt der Fund in einer im MR geänderten Datei — auch
    /// außerhalb der Hunks? <c>InDiff</c> impliziert immer <c>InChangedFile</c>.</summary>
    public bool InChangedFile { get; init; }
}
