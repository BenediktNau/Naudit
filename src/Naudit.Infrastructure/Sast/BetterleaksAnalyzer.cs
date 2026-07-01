using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Sast;

/// <summary>Secrets-Detection über Betterleaks: `betterleaks dir . --report-format json
/// --report-path /dev/stdout --no-banner` im Workspace-Root (rein statisch, kein Build,
/// keine git-History — nur der ausgecheckte Stand). Eingebaute Regeln, kein Regelset nötig.
/// Betterleaks ist der vom ursprünglichen Gitleaks-Autor gepflegte Drop-in-Nachfolger
/// (gleiche CLI/Config, aktuelle Dependencies) und ersetzt das gebündelte Gitleaks-Binary.
/// <b>Wichtig:</b> der rohe Secret-/Match-Wert wird NIE in den `ScanFinding` übernommen —
/// er ginge sonst in den LLM-Prompt und in Logs. Fehler ⇒ leere Liste (geloggt).</summary>
public sealed class BetterleaksAnalyzer(
    IProcessRunner runner, ILogger<BetterleaksAnalyzer> logger, TimeSpan timeout) : ISastAnalyzer
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Name => "betterleaks";

    public async Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        var spec = new ProcessSpec(
            "betterleaks", ["dir", ".", "--report-format", "json", "--report-path", "/dev/stdout", "--no-banner"],
            StdIn: null, Environment: null, WorkingDirectory: workspace.RootPath, Timeout: timeout);

        ProcessResult result;
        try { result = await runner.RunAsync(spec, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // Abbruch propagieren, nicht schlucken
        catch (Exception ex) { logger.LogWarning(ex, "betterleaks nicht ausführbar"); return []; }

        // Betterleaks (wie Gitleaks): Exit 0 (keine Funde) oder 1 (Funde) liefern den JSON-Report; höhere Codes = echter Fehler.
        if (result.ExitCode > 1 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            if (result.ExitCode > 1)
                logger.LogWarning("betterleaks Exit {Code}: {Err}", result.ExitCode, result.StdErr);
            return [];
        }

        List<BetterleaksFinding>? report;
        try { report = JsonSerializer.Deserialize<List<BetterleaksFinding>>(result.StdOut, JsonOpts); }
        catch (JsonException ex) { logger.LogWarning(ex, "betterleaks JSON nicht parsebar"); return []; }

        if (report is null)
            return [];

        // Bewusst nur RuleID/Description/Datei/Zeile — niemals Secret/Match.
        return report.Select(r => new ScanFinding(
            Tool: "betterleaks",
            Category: FindingCategory.Secrets,
            Severity: FindingSeverity.High,           // Secrets sind generell hochkritisch; das Tool liefert keine Severity
            Message: r.Description ?? "potential secret",
            RuleId: r.RuleId,
            FilePath: Normalize(r.File),
            Line: r.StartLine)).ToList();
    }

    private static string? Normalize(string? path)
        => path is null ? null : path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;

    // Nur die unbedenklichen Felder gebunden; Secret/Match absichtlich NICHT.
    private sealed record BetterleaksFinding(
        [property: JsonPropertyName("RuleID")] string? RuleId,
        [property: JsonPropertyName("Description")] string? Description,
        [property: JsonPropertyName("File")] string? File,
        [property: JsonPropertyName("StartLine")] int? StartLine);
}
