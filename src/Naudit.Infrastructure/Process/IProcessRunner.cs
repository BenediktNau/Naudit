namespace Naudit.Infrastructure.Process;

/// <summary>Dünne, testbare Naht über einen Subprozess (vermeidet echtes `claude` im Test).</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default);
}

/// <param name="Environment">Additiv zur geerbten Prozess-Umgebung.</param>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? StdIn,
    IReadOnlyDictionary<string, string?>? Environment,
    string? WorkingDirectory,
    TimeSpan Timeout);

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
