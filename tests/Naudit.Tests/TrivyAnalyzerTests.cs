using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Sast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class TrivyAnalyzerTests
{
    private sealed class Ws(string root) : Naudit.Core.Abstractions.IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private const string Json = """
    { "Results": [
        { "Target": "packages.lock.json",
          "Vulnerabilities": [
            { "VulnerabilityID": "CVE-2024-1234", "PkgName": "Newtonsoft.Json",
              "InstalledVersion": "9.0.1", "Severity": "CRITICAL", "Title": "RCE in parser" } ] } ] }
    """;

    [Fact]
    public async Task AnalyzeAsync_mapsTrivyVuln_toScaFinding()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, Json, ""));
        var analyzer = new TrivyAnalyzer(runner, NullLogger<TrivyAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        var findings = await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        var f = Assert.Single(findings);
        Assert.Equal("trivy", f.Tool);
        Assert.Equal(FindingCategory.Sca, f.Category);
        Assert.Equal(FindingSeverity.Critical, f.Severity);
        Assert.Equal("CVE-2024-1234", f.RuleId);
        Assert.Equal("packages.lock.json", f.FilePath);
        Assert.Contains("Newtonsoft.Json", f.Message);
        Assert.Contains("9.0.1", f.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_returnsEmpty_whenNoResults()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, """{ "Results": [] }""", ""));
        var analyzer = new TrivyAnalyzer(runner, NullLogger<TrivyAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
    }
}
