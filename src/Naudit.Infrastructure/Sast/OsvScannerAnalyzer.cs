using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Sast;

/// <summary>SCA über OSV-Scanner (Google): `osv-scanner scan source --format json -r .` im
/// Workspace-Root. Sprach-agnostisch über Lockfiles/Manifeste (ergänzt trivy/dotnet-sca). Rein
/// statisch (kein Build). Ein Fund je OSV-„group" (zusammengefasste Aliase einer Schwachstelle).
/// Fehler ⇒ leere Liste (geloggt); 128 = keine scannbaren Quellen (kein Fehler).</summary>
public sealed class OsvScannerAnalyzer(
    IProcessRunner runner, ILogger<OsvScannerAnalyzer> logger, TimeSpan timeout) : ISastAnalyzer
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Name => "osv-scanner";

    public async Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        var spec = new ProcessSpec(
            "osv-scanner", ["scan", "source", "--format", "json", "-r", "."],
            StdIn: null, Environment: null, WorkingDirectory: workspace.RootPath, Timeout: timeout);

        ProcessResult result;
        try { result = await runner.RunAsync(spec, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // Abbruch propagieren
        catch (Exception ex) { logger.LogWarning(ex, "osv-scanner nicht ausführbar"); return []; }

        // Exit 0 (keine Funde) / 1 (Funde) liefern JSON; 128 = keine Lockfiles (ok, leer); sonst Fehler.
        if (result.ExitCode is not (0 or 1) || string.IsNullOrWhiteSpace(result.StdOut))
        {
            if (result.ExitCode is not (0 or 1 or 128))
                logger.LogWarning("osv-scanner Exit {Code}: {Err}", result.ExitCode, result.StdErr);
            return [];
        }

        OsvReport? report;
        try { report = JsonSerializer.Deserialize<OsvReport>(result.StdOut, JsonOpts); }
        catch (JsonException ex) { logger.LogWarning(ex, "osv-scanner JSON nicht parsebar"); return []; }

        if (report?.Results is null)
            return [];

        var findings = new List<ScanFinding>();
        foreach (var res in report.Results)
        {
            var file = Relative(workspace.RootPath, res.Source?.Path);
            foreach (var pkg in res.Packages ?? [])
            {
                var p = pkg.Package;
                var label = p is null ? "dependency" : $"{p.Name} {p.Version} ({p.Ecosystem})";
                foreach (var group in pkg.Groups ?? [])
                {
                    findings.Add(new ScanFinding(
                        Tool: "osv-scanner",
                        Category: FindingCategory.Sca,
                        Severity: MapSeverity(group.MaxSeverity),
                        Message: label,
                        RuleId: PickId(group),
                        FilePath: file,
                        Line: null));
                }
            }
        }
        return findings;
    }

    // OSV liefert absolute Pfade — relativ zur Workspace-Root machen (analog zu den übrigen Analyzern).
    private static string? Relative(string root, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return Path.GetRelativePath(root, path); }
        catch { return path; }
    }

    // CVE bevorzugen (wie trivy: RuleId = CVE), sonst erste ID der Gruppe.
    private static string? PickId(OsvGroup group)
        => group.Aliases?.FirstOrDefault(a => a.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
           ?? group.Ids?.FirstOrDefault();

    // CVSS-Base-Score (String) → normalisierter Schweregrad; fehlt/unparsebar ⇒ Medium (SCA ist nicht trivial).
    private static FindingSeverity MapSeverity(string? maxSeverity)
    {
        if (!double.TryParse(maxSeverity, NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
            return FindingSeverity.Medium;
        return s switch
        {
            >= 9.0 => FindingSeverity.Critical,
            >= 7.0 => FindingSeverity.High,
            >= 4.0 => FindingSeverity.Medium,
            > 0.0 => FindingSeverity.Low,
            _ => FindingSeverity.Info,
        };
    }

    private sealed record OsvReport([property: JsonPropertyName("results")] List<OsvResult>? Results);
    private sealed record OsvResult(
        [property: JsonPropertyName("source")] OsvSource? Source,
        [property: JsonPropertyName("packages")] List<OsvPackage>? Packages);
    private sealed record OsvSource([property: JsonPropertyName("path")] string? Path);
    private sealed record OsvPackage(
        [property: JsonPropertyName("package")] OsvPkgInfo? Package,
        [property: JsonPropertyName("groups")] List<OsvGroup>? Groups);
    private sealed record OsvPkgInfo(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("ecosystem")] string? Ecosystem);
    private sealed record OsvGroup(
        [property: JsonPropertyName("ids")] List<string>? Ids,
        [property: JsonPropertyName("aliases")] List<string>? Aliases,
        [property: JsonPropertyName("max_severity")] string? MaxSeverity);
}
