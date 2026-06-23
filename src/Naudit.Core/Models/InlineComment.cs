namespace Naudit.Core.Models;

/// <summary>Ein Review-Kommentar, der an eine konkrete Diff-Zeile gebunden ist.</summary>
/// <param name="OldLine">Zeilennummer in der alten Datei — nur bei Kontextzeilen gesetzt, bei hinzugefügten Zeilen null.</param>
public sealed record InlineComment(string FilePath, int NewLine, int? OldLine, string Body);
