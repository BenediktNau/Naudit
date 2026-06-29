namespace Naudit.Core.Abstractions;

/// <summary>Maskiert sensible Werte (Secrets/Keys/Passwörter, IP-Adressen, E-Mails) in Text, der in
/// den LLM-Prompt geht. <b>Line-preserving:</b> fügt/entfernt nie Zeilen und lässt Diff-Strukturzeilen
/// unangetastet, damit die New-File-Zeilennummerierung für Inline-Kommentare erhalten bleibt.
/// Seam analog <see cref="IFindingReducer"/>: Default jetzt deterministisch (Regex/Entropie),
/// später per Config gegen einen Presidio-/LLM-Redactor tauschbar — ohne Vertragsänderung.</summary>
public interface IPromptRedactor
{
    Task<string> RedactAsync(string text, CancellationToken ct = default);
}

/// <summary>No-Op-Redactor (Identity): liefert den Text unverändert. Der Aus-Fall der Redaction
/// (registriert bei <c>Naudit:Redaction:Enabled=false</c>) ⇒ exakt das bisherige diff-only-Verhalten.</summary>
public sealed class NullPromptRedactor : IPromptRedactor
{
    public Task<string> RedactAsync(string text, CancellationToken ct = default) => Task.FromResult(text);
}
