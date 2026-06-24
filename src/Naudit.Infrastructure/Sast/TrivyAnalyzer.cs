using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Sast;

/// <summary>SCA über Trivy: `trivy fs --scanners vuln --format json --quiet .` im Workspace-Root.
/// Führt fremden Code NICHT aus. Fehler ⇒ leere Liste (geloggt).</summary>
public sealed class TrivyAnalyzer(IProcessRunner runner, ILogger<TrivyAnalyzer> logger, TimeSpan timeout) : ISastAnalyzer
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Name => "trivy";

    public async Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        var spec = new ProcessSpec(
            "trivy", ["fs", "--scanners", "vuln", "--format", "json", "--quiet", "."],
            StdIn: null, Environment: null, WorkingDirectory: workspace.RootPath, Timeout: timeout);

        ProcessResult result;
        try { result = await runner.RunAsync(spec, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "trivy nicht ausführbar"); return []; }

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            logger.LogWarning("trivy Exit {Code}: {Err}", result.ExitCode, result.StdErr);
            return [];
        }

        TrivyReport? report;
        try { report = JsonSerializer.Deserialize<TrivyReport>(result.StdOut, JsonOpts); }
        catch (JsonException ex) { logger.LogWarning(ex, "trivy JSON nicht parsebar"); return []; }

        if (report?.Results is null)
            return [];

        return report.Results
            .Where(r => r.Vulnerabilities is not null)
            .SelectMany(r => r.Vulnerabilities!.Select(v => new ScanFinding(
                Tool: "trivy",
                Category: FindingCategory.Sca,
                Severity: MapSeverity(v.Severity),
                Message: $"{v.PkgName} {v.InstalledVersion}: {v.Title}".Trim(),
                RuleId: v.VulnerabilityId,
                FilePath: r.Target,
                Line: null)))
            .ToList();
    }

    private static FindingSeverity MapSeverity(string? s) => s?.ToUpperInvariant() switch
    {
        "CRITICAL" => FindingSeverity.Critical,
        "HIGH" => FindingSeverity.High,
        "MEDIUM" => FindingSeverity.Medium,
        "LOW" => FindingSeverity.Low,
        _ => FindingSeverity.Info,
    };

    private sealed record TrivyReport([property: JsonPropertyName("Results")] List<TrivyTarget>? Results);
    private sealed record TrivyTarget(
        [property: JsonPropertyName("Target")] string? Target,
        [property: JsonPropertyName("Vulnerabilities")] List<TrivyVuln>? Vulnerabilities);
    private sealed record TrivyVuln(
        [property: JsonPropertyName("VulnerabilityID")] string? VulnerabilityId,
        [property: JsonPropertyName("PkgName")] string? PkgName,
        [property: JsonPropertyName("InstalledVersion")] string? InstalledVersion,
        [property: JsonPropertyName("Severity")] string? Severity,
        [property: JsonPropertyName("Title")] string? Title);
}
