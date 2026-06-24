using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Sast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class SemgrepAnalyzerTests
{
    private sealed class Ws(string root) : Naudit.Core.Abstractions.IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private const string Json = """
    { "results": [
        { "check_id": "csharp.lang.security.sqli",
          "path": "src/Foo.cs",
          "start": { "line": 42 },
          "extra": { "message": "SQL injection", "severity": "ERROR" } }
      ], "errors": [] }
    """;

    [Fact]
    public async Task AnalyzeAsync_mapsSemgrepResult_toSastFinding()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, Json, ""));
        var analyzer = new SemgrepAnalyzer(runner, NullLogger<SemgrepAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        var findings = await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        var f = Assert.Single(findings);
        Assert.Equal("semgrep", f.Tool);
        Assert.Equal(FindingCategory.Sast, f.Category);
        Assert.Equal(FindingSeverity.High, f.Severity);
        Assert.Equal("src/Foo.cs", f.FilePath);
        Assert.Equal(42, f.Line);
        Assert.Equal("csharp.lang.security.sqli", f.RuleId);
        Assert.Contains("SQL injection", f.Message);
        Assert.Equal("/tmp/x", runner.LastSpec!.WorkingDirectory);
    }

    [Fact]
    public async Task AnalyzeAsync_returnsEmpty_whenToolFails()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(2, "", "semgrep crashed"));
        var analyzer = new SemgrepAnalyzer(runner, NullLogger<SemgrepAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        var findings = await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        Assert.Empty(findings);
    }
}
