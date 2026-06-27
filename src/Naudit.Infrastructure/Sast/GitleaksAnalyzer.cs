using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Sast;

/// <summary>Secrets-Detection über Gitleaks: `gitleaks dir . --report-format json
/// --report-path /dev/stdout --no-banner` im Workspace-Root (rein statisch, kein Build,
/// keine git-History — nur der ausgecheckte Stand). Eingebaute Regeln, kein Regelset nötig.
/// <b>Wichtig:</b> der rohe Secret-/Match-Wert wird NIE in den `ScanFinding` übernommen —
/// er ginge sonst in den LLM-Prompt und in Logs. Fehler ⇒ leere Liste (geloggt).</summary>
public sealed class GitleaksAnalyzer(
    IProcessRunner runner, ILogger<GitleaksAnalyzer> logger, TimeSpan timeout) : ISastAnalyzer
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Name => "gitleaks";

    public async Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        var spec = new ProcessSpec(
            "gitleaks", ["dir", ".", "--report-format", "json", "--report-path", "/dev/stdout", "--no-banner"],
            StdIn: null, Environment: null, WorkingDirectory: workspace.RootPath, Timeout: timeout);

        ProcessResult result;
        try { result = await runner.RunAsync(spec, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "gitleaks nicht ausführbar"); return []; }

        // Gitleaks: Exit 0 (keine Funde) oder 1 (Funde) liefern den JSON-Report; höhere Codes = echter Fehler.
        if (result.ExitCode > 1 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            if (result.ExitCode > 1)
                logger.LogWarning("gitleaks Exit {Code}: {Err}", result.ExitCode, result.StdErr);
            return [];
        }

        List<GitleaksFinding>? report;
        try { report = JsonSerializer.Deserialize<List<GitleaksFinding>>(result.StdOut, JsonOpts); }
        catch (JsonException ex) { logger.LogWarning(ex, "gitleaks JSON nicht parsebar"); return []; }

        if (report is null)
            return [];

        // Bewusst nur RuleID/Description/Datei/Zeile — niemals Secret/Match.
        return report.Select(r => new ScanFinding(
            Tool: "gitleaks",
            Category: FindingCategory.Secrets,
            Severity: FindingSeverity.High,           // Secrets sind generell hochkritisch; Gitleaks liefert keine Severity
            Message: r.Description ?? "potential secret",
            RuleId: r.RuleId,
            FilePath: Normalize(r.File),
            Line: r.StartLine)).ToList();
    }

    private static string? Normalize(string? path)
        => path is null ? null : path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;

    // Nur die unbedenklichen Felder gebunden; Secret/Match absichtlich NICHT.
    private sealed record GitleaksFinding(
        [property: JsonPropertyName("RuleID")] string? RuleId,
        [property: JsonPropertyName("Description")] string? Description,
        [property: JsonPropertyName("File")] string? File,
        [property: JsonPropertyName("StartLine")] int? StartLine);
}
