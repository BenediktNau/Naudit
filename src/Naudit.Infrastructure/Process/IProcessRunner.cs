namespace Naudit.Infrastructure.Process;

/// <summary>Dünne, testbare Naht über einen Subprozess (vermeidet echtes `claude` im Test).</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default);
}

/// <param name="Environment">Additiv zur (ggf. gefilterten) geerbten Prozess-Umgebung.</param>
/// <param name="InheritedEnvironment">null (Default) ⇒ die volle Host-Umgebung wird geerbt (heutiges
/// Verhalten). Gesetzt ⇒ nur diese Variablen werden geerbt (Groß-/Kleinschreibung egal; ein Eintrag
/// mit abschließendem <c>*</c> ist ein Präfix), alles andere — insbesondere Naudits eigene Secrets
/// (<c>Naudit__*</c>) — sieht der Kindprozess nicht. Für Prozesse über fremdem Code (git, Analyzer).</param>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? StdIn,
    IReadOnlyDictionary<string, string?>? Environment,
    string? WorkingDirectory,
    TimeSpan Timeout,
    IReadOnlyList<string>? InheritedEnvironment = null);

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
