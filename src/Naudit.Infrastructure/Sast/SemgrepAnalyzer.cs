using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Sast;

/// <summary>SAST über Semgrep: `semgrep --config auto --json .` im Workspace-Root.
/// Führt fremden Code NICHT aus (rein statisch). Fehler ⇒ leere Liste (geloggt).</summary>
public sealed class SemgrepAnalyzer(IProcessRunner runner, ILogger<SemgrepAnalyzer> logger, TimeSpan timeout) : ISastAnalyzer
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Name => "semgrep";

    public async Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        var spec = new ProcessSpec(
            "semgrep", ["--config", "auto", "--json", "."],
            StdIn: null, Environment: null, WorkingDirectory: workspace.RootPath, Timeout: timeout);

        ProcessResult result;
        try { result = await runner.RunAsync(spec, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "semgrep nicht ausführbar"); return []; }

        // Semgrep liefert Exit 0 (keine Funde) oder 1 (Funde) mit JSON; höhere Codes = echter Fehler.
        if (result.ExitCode > 1 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            logger.LogWarning("semgrep Exit {Code}: {Err}", result.ExitCode, result.StdErr);
            return [];
        }

        SemgrepReport? report;
        try { report = JsonSerializer.Deserialize<SemgrepReport>(result.StdOut, JsonOpts); }
        catch (JsonException ex) { logger.LogWarning(ex, "semgrep JSON nicht parsebar"); return []; }

        if (report?.Results is null)
            return [];

        return report.Results.Select(r => new ScanFinding(
            Tool: "semgrep",
            Category: FindingCategory.Sast,
            Severity: MapSeverity(r.Extra?.Severity),
            Message: r.Extra?.Message ?? r.CheckId ?? "semgrep finding",
            RuleId: r.CheckId,
            FilePath: Normalize(r.Path),
            Line: r.Start?.Line)).ToList();
    }

    private static string? Normalize(string? path)
        => path is null ? null : path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;

    private static FindingSeverity MapSeverity(string? s) => s?.ToUpperInvariant() switch
    {
        "ERROR" => FindingSeverity.High,
        "WARNING" => FindingSeverity.Medium,
        "INFO" => FindingSeverity.Low,
        _ => FindingSeverity.Info,
    };

    private sealed record SemgrepReport([property: JsonPropertyName("results")] List<SemgrepResult>? Results);
    private sealed record SemgrepResult(
        [property: JsonPropertyName("check_id")] string? CheckId,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("start")] SemgrepStart? Start,
        [property: JsonPropertyName("extra")] SemgrepExtra? Extra);
    private sealed record SemgrepStart([property: JsonPropertyName("line")] int Line);
    private sealed record SemgrepExtra(
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("severity")] string? Severity);
}
