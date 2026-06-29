namespace Naudit.Core.Models;

/// <summary>Wie sicher ist sich das LLM, dass ein Fund real ist (Reihenfolge = Rang für ≥-Vergleich).</summary>
public enum ReviewConfidence { Low, Medium, High }

/// <summary>Ein Review-Kommentar, der an eine konkrete Diff-Zeile gebunden ist.</summary>
/// <param name="OldLine">Zeilennummer in der alten Datei — nur bei Kontextzeilen gesetzt, bei hinzugefügten Zeilen null.</param>
/// <param name="Severity">Schweregrad des Funds (für das severity-bewusste Gate; default Info = nicht blockierend).</param>
/// <param name="Confidence">Sicherheit des LLM (default Low = nicht blockierend).</param>
public sealed record InlineComment(
    string FilePath,
    int NewLine,
    int? OldLine,
    string Body,
    FindingSeverity Severity = FindingSeverity.Info,
    ReviewConfidence Confidence = ReviewConfidence.Low);
