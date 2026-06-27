using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Sast;

/// <summary>SAST über OpenGrep (voll-LGPL-Fork von Semgrep):
/// `opengrep scan --config &lt;regeln&gt; [...] --json .` im Workspace-Root.
/// Bewusst <b>kein</b> `--config auto` — das zöge die lizenzbelasteten Registry-Regeln + Telemetrie;
/// stattdessen explizit gepinnte Regelpfade (Image: opengrep-rules + eigenes Overlay).
/// Führt fremden Code NICHT aus (rein statisch). Fehler ⇒ leere Liste (geloggt).
/// JSON-Format ist Semgrep-kompatibel, daher identisches Parsing/Severity-Mapping.</summary>
public sealed class OpengrepAnalyzer(
    IProcessRunner runner,
    ILogger<OpengrepAnalyzer> logger,
    TimeSpan timeout,
    IReadOnlyList<string> rulesPaths) : ISastAnalyzer
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Name => "opengrep";

    public async Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        // scan --config <p1> [--config <p2> ...] --json .
        var args = new List<string> { "scan" };
        foreach (var path in rulesPaths)
        {
            args.Add("--config");
            args.Add(path);
        }
        args.Add("--json");
        args.Add(".");

        var spec = new ProcessSpec(
            "opengrep", args,
            StdIn: null, Environment: null, WorkingDirectory: workspace.RootPath, Timeout: timeout);

        ProcessResult result;
        try { result = await runner.RunAsync(spec, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "opengrep nicht ausführbar"); return []; }

        // OpenGrep liefert wie Semgrep Exit 0 (keine Funde) oder 1 (Funde) mit JSON; höhere Codes = echter Fehler.
        if (result.ExitCode > 1 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            logger.LogWarning("opengrep Exit {Code}: {Err}", result.ExitCode, result.StdErr);
            return [];
        }

        OpengrepReport? report;
        try { report = JsonSerializer.Deserialize<OpengrepReport>(result.StdOut, JsonOpts); }
        catch (JsonException ex) { logger.LogWarning(ex, "opengrep JSON nicht parsebar"); return []; }

        if (report?.Results is null)
            return [];

        return report.Results.Select(r => new ScanFinding(
            Tool: "opengrep",
            Category: FindingCategory.Sast,
            Severity: MapSeverity(r.Extra?.Severity),
            Message: r.Extra?.Message ?? r.CheckId ?? "opengrep finding",
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

    private sealed record OpengrepReport([property: JsonPropertyName("results")] List<OpengrepResult>? Results);
    private sealed record OpengrepResult(
        [property: JsonPropertyName("check_id")] string? CheckId,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("start")] OpengrepStart? Start,
        [property: JsonPropertyName("extra")] OpengrepExtra? Extra);
    private sealed record OpengrepStart([property: JsonPropertyName("line")] int Line);
    private sealed record OpengrepExtra(
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("severity")] string? Severity);
}
