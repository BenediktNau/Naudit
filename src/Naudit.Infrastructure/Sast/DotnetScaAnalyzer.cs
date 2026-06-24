using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Sast;

/// <summary>SCA für .NET: `dotnet restore` (baut/restored fremden Code!) dann
/// `dotnet list package --vulnerable --include-transitive --format json`. Opt-in. Fehler ⇒ leere Liste.</summary>
public sealed class DotnetScaAnalyzer(IProcessRunner runner, ILogger<DotnetScaAnalyzer> logger, TimeSpan timeout) : ISastAnalyzer
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Name => "dotnet-sca";

    public async Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        var restore = await RunDotnetAsync(workspace.RootPath, ct, "restore");
        if (restore is null || restore.ExitCode != 0)
        {
            logger.LogWarning("dotnet restore fehlgeschlagen (Exit {Code})", restore?.ExitCode);
            return [];
        }

        var list = await RunDotnetAsync(workspace.RootPath, ct,
            "list", "package", "--vulnerable", "--include-transitive", "--format", "json");
        if (list is null || list.ExitCode != 0 || string.IsNullOrWhiteSpace(list.StdOut))
        {
            logger.LogWarning("dotnet list package fehlgeschlagen (Exit {Code})", list?.ExitCode);
            return [];
        }

        DotnetListReport? report;
        try { report = JsonSerializer.Deserialize<DotnetListReport>(list.StdOut, JsonOpts); }
        catch (JsonException ex) { logger.LogWarning(ex, "dotnet list JSON nicht parsebar"); return []; }

        if (report?.Projects is null)
            return [];

        return (
            from p in report.Projects
            from fw in p.Frameworks ?? []
            from pkg in (fw.TopLevelPackages ?? []).Concat(fw.TransitivePackages ?? [])
            from v in pkg.Vulnerabilities ?? []
            select new ScanFinding(
                Tool: "dotnet-sca",
                Category: FindingCategory.Sca,
                Severity: MapSeverity(v.Severity),
                Message: $"{pkg.Id} {pkg.ResolvedVersion}: {v.Severity} vulnerability",
                RuleId: LastSegment(v.AdvisoryUrl),
                FilePath: p.Path,
                Line: null)).ToList();
    }

    private async Task<ProcessResult?> RunDotnetAsync(string root, CancellationToken ct, params string[] args)
    {
        var spec = new ProcessSpec("dotnet", args, StdIn: null, Environment: null, WorkingDirectory: root, Timeout: timeout);
        try { return await runner.RunAsync(spec, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "dotnet nicht ausführbar"); return null; }
    }

    private static string? LastSegment(string? url)
        => string.IsNullOrEmpty(url) ? null : url.TrimEnd('/').Split('/').Last();

    private static FindingSeverity MapSeverity(string? s) => s?.ToUpperInvariant() switch
    {
        "CRITICAL" => FindingSeverity.Critical,
        "HIGH" => FindingSeverity.High,
        "MODERATE" => FindingSeverity.Medium,
        "LOW" => FindingSeverity.Low,
        _ => FindingSeverity.Info,
    };

    private sealed record DotnetListReport([property: JsonPropertyName("projects")] List<DotnetProject>? Projects);
    private sealed record DotnetProject(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("frameworks")] List<DotnetFramework>? Frameworks);
    private sealed record DotnetFramework(
        [property: JsonPropertyName("topLevelPackages")] List<DotnetPackage>? TopLevelPackages,
        [property: JsonPropertyName("transitivePackages")] List<DotnetPackage>? TransitivePackages);
    private sealed record DotnetPackage(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("resolvedVersion")] string? ResolvedVersion,
        [property: JsonPropertyName("vulnerabilities")] List<DotnetVuln>? Vulnerabilities);
    private sealed record DotnetVuln(
        [property: JsonPropertyName("severity")] string? Severity,
        [property: JsonPropertyName("advisoryurl")] string? AdvisoryUrl);
}
