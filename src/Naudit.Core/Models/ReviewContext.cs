namespace Naudit.Core.Models;

/// <summary>Read-only-Kontext zu einer Änderung: umgebender Code, Call-Sites, Repo-Überblick.</summary>
public sealed record ReviewContext(
    IReadOnlyList<FileEnvironment> Environments,
    IReadOnlyList<SymbolUsage> Usages,
    string? Overview)
{
    /// <summary>Leerer Kontext (Feature aus / Checkout fehlgeschlagen / kein Fund) — rendert nichts.</summary>
    public static readonly ReviewContext Empty = new([], [], null);
}

/// <summary>Umgebender Code einer geänderten Datei: ganze Datei oder ein Block-Ausschnitt ab StartLine.</summary>
public sealed record FileEnvironment(string FilePath, int StartLine, string Content, bool IsFullFile);

/// <summary>Eine Verwendungsstelle eines im Diff deklarierten Symbols (1-basierte Zeile).</summary>
public sealed record SymbolUsage(string Symbol, string FilePath, int Line, string Snippet);
