namespace Naudit.Infrastructure.Redaction;

/// <summary>Konfiguration der Prompt-Redaction (Section <c>Naudit:Redaction</c>).</summary>
public sealed class RedactionOptions
{
    /// <summary>Redaction global an/aus. <b>Default an</b> (safe-by-default für ein Privacy-Feature).
    /// Aus ⇒ es wird ein <c>NullPromptRedactor</c> registriert ⇒ exakt das bisherige Verhalten.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Shannon-Entropie-Schwelle (Bits/Zeichen) für den Token-Fallback. Höher = strenger
    /// (weniger False-Positives, mehr potenzielle False-Negatives).</summary>
    public double EntropyThreshold { get; set; } = 4.0;

    /// <summary>Nur token-artige Substrings ab dieser Länge werden vom Entropie-Pass geprüft —
    /// schützt normale Bezeichner/kurze Strings vor versehentlicher Maskierung.</summary>
    public int MinEntropyTokenLength { get; set; } = 20;
}
