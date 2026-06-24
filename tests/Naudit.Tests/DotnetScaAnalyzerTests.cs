using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Sast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class DotnetScaAnalyzerTests
{
    private sealed class Ws(string root) : Naudit.Core.Abstractions.IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private const string ListJson = """
    { "projects": [
        { "path": "src/App.csproj",
          "frameworks": [
            { "framework": "net10.0",
              "transitivePackages": [
                { "id": "Some.Pkg", "resolvedVersion": "1.0.0",
                  "vulnerabilities": [ { "severity": "High", "advisoryurl": "https://ghsa/GHSA-xxxx" } ] } ] } ] } ] }
    """;

    [Fact]
    public async Task AnalyzeAsync_mapsVulnerablePackage_toScaFinding()
    {
        // restore → ok; list → JSON.
        var runner = new StubProcessRunner(s =>
            string.Join(" ", s.Arguments).Contains("list")
                ? new ProcessResult(0, ListJson, "")
                : new ProcessResult(0, "", ""));
        var analyzer = new DotnetScaAnalyzer(runner, NullLogger<DotnetScaAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        var findings = await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        var f = Assert.Single(findings);
        Assert.Equal("dotnet-sca", f.Tool);
        Assert.Equal(FindingCategory.Sca, f.Category);
        Assert.Equal(FindingSeverity.High, f.Severity);
        Assert.Contains("Some.Pkg", f.Message);
        Assert.Contains("1.0.0", f.Message);
        Assert.Equal("GHSA-xxxx", f.RuleId);
    }

    [Fact]
    public async Task AnalyzeAsync_returnsEmpty_whenRestoreFails()
    {
        var runner = new StubProcessRunner(s =>
            string.Join(" ", s.Arguments).Contains("restore")
                ? new ProcessResult(1, "", "restore failed")
                : new ProcessResult(0, ListJson, ""));
        var analyzer = new DotnetScaAnalyzer(runner, NullLogger<DotnetScaAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
    }
}
