using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Sast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class OpengrepAnalyzerTests
{
    private sealed class Ws(string root) : Naudit.Core.Abstractions.IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Echter `opengrep scan --json`-Output (Semgrep-kompatibel; mit echtem Binary v1.23.0 eingefangen).
    private const string Json = """
    { "version": "1.23.0",
      "results": [
        { "check_id": "rules.dotnet-weak-hash",
          "path": "src/Crypto.cs",
          "start": { "line": 12, "col": 35, "offset": 70 },
          "end": { "line": 12, "col": 45 },
          "extra": { "message": "MD5 ist gebrochen - SHA-256+ verwenden.", "severity": "WARNING" } }
      ], "errors": [] }
    """;

    [Fact]
    public async Task AnalyzeAsync_mapsOpengrepResult_toSastFinding()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(1, Json, ""));
        var analyzer = new OpengrepAnalyzer(
            runner, NullLogger<OpengrepAnalyzer>.Instance, TimeSpan.FromMinutes(5),
            ["/opt/opengrep-rules", "/opt/naudit-rules"]);

        var findings = await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        var f = Assert.Single(findings);
        Assert.Equal("opengrep", f.Tool);
        Assert.Equal(FindingCategory.Sast, f.Category);
        Assert.Equal(FindingSeverity.Medium, f.Severity);   // WARNING -> Medium
        Assert.Equal("src/Crypto.cs", f.FilePath);
        Assert.Equal(12, f.Line);
        Assert.Equal("rules.dotnet-weak-hash", f.RuleId);
        Assert.Contains("MD5", f.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_invokesScan_withExplicitConfigAndJson_notConfigAuto()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, """{ "results": [], "errors": [] }""", ""));
        var analyzer = new OpengrepAnalyzer(
            runner, NullLogger<OpengrepAnalyzer>.Instance, TimeSpan.FromMinutes(5),
            ["/opt/opengrep-rules", "/opt/naudit-rules"]);

        await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        var spec = runner.LastSpec!;
        Assert.Equal("opengrep", spec.FileName);
        Assert.Equal("/tmp/x", spec.WorkingDirectory);
        Assert.Equal(
            new[] { "scan", "--config", "/opt/opengrep-rules", "--config", "/opt/naudit-rules", "--json", "." },
            spec.Arguments);
        Assert.DoesNotContain("auto", spec.Arguments);   // niemals die lizenzbelastete Registry
    }

    [Fact]
    public async Task AnalyzeAsync_returnsEmpty_whenToolFails()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(2, "", "opengrep crashed"));
        var analyzer = new OpengrepAnalyzer(
            runner, NullLogger<OpengrepAnalyzer>.Instance, TimeSpan.FromMinutes(5), ["/opt/opengrep-rules"]);

        var findings = await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        Assert.Empty(findings);
    }
}
