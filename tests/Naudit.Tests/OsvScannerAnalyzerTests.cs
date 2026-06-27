using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Sast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class OsvScannerAnalyzerTests
{
    private sealed class Ws(string root) : Naudit.Core.Abstractions.IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Echtes `osv-scanner scan source --format json`-Schema (gekürzt; Pfade sind ABSOLUT).
    private const string Json = """
    { "results": [
        { "source": { "path": "/work/requirements.txt", "type": "lockfile" },
          "packages": [
            { "package": { "name": "django", "version": "2.0", "ecosystem": "PyPI" },
              "groups": [ { "ids": ["PYSEC-2018-2","GHSA-5hg3-6c2f-f3wr"],
                           "aliases": ["CVE-2018-14574","GHSA-5hg3-6c2f-f3wr","PYSEC-2018-2"],
                           "max_severity": "6.1" } ],
              "vulnerabilities": [ { "id": "PYSEC-2018-2" } ] } ] } ] }
    """;

    [Fact]
    public async Task AnalyzeAsync_mapsOsvGroup_toScaFinding()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(1, Json, "")); // Exit 1 = Funde
        var analyzer = new OsvScannerAnalyzer(runner, NullLogger<OsvScannerAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        var f = Assert.Single(await analyzer.AnalyzeAsync(new Ws("/work"), []));

        Assert.Equal("osv-scanner", f.Tool);
        Assert.Equal(FindingCategory.Sca, f.Category);
        Assert.Equal(FindingSeverity.Medium, f.Severity);           // CVSS 6.1 -> Medium
        Assert.Equal("CVE-2018-14574", f.RuleId);                   // CVE-Alias bevorzugt
        Assert.Equal("requirements.txt", f.FilePath);               // relativ zur Workspace-Root
        Assert.Null(f.Line);
        Assert.Contains("django", f.Message);
        Assert.Contains("2.0", f.Message);
        Assert.Contains("PyPI", f.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_invokesScanSource_jsonRecursive()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, """{ "results": [] }""", ""));
        var analyzer = new OsvScannerAnalyzer(runner, NullLogger<OsvScannerAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        await analyzer.AnalyzeAsync(new Ws("/work"), []);

        var spec = runner.LastSpec!;
        Assert.Equal("osv-scanner", spec.FileName);
        Assert.Equal("/work", spec.WorkingDirectory);
        Assert.Equal(new[] { "scan", "source", "--format", "json", "-r", "." }, spec.Arguments);
    }

    [Fact]
    public async Task AnalyzeAsync_returnsEmpty_whenNoLockfiles_exit128()
    {
        // 128 = "keine scannbaren Quellen" — kein Fehler, nur nichts zu tun.
        var runner = new StubProcessRunner(_ => new ProcessResult(128, "", ""));
        var analyzer = new OsvScannerAnalyzer(runner, NullLogger<OsvScannerAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/work"), []));
    }

    [Fact]
    public async Task AnalyzeAsync_returnsEmpty_whenToolErrors()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(127, "", "osv-scanner crashed"));
        var analyzer = new OsvScannerAnalyzer(runner, NullLogger<OsvScannerAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/work"), []));
    }

    [Fact]
    public async Task AnalyzeAsync_propagatesCancellation_insteadOfSwallowingAsEmpty()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new StubProcessRunner(_ => throw new OperationCanceledException(cts.Token));
        var analyzer = new OsvScannerAnalyzer(runner, NullLogger<OsvScannerAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync(new Ws("/work"), [], cts.Token));
    }
}
