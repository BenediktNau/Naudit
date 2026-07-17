namespace Naudit.Core.Models;

public enum MemoryKind { FalsePositive, Convention }

/// <summary>Ein Gedächtnis-Eintrag: als False Positive markierter Fund oder Projekt-Konvention.
/// File ist bei Konventionen reine Anzeige-Einordnung, nie Auswahlfilter.</summary>
public sealed record MemoryEntry(MemoryKind Kind, string? File, string Text, string? Reason);
