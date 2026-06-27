using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Sast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitleaksAnalyzerTests
{
    private sealed class Ws(string root) : Naudit.Core.Abstractions.IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private const string FakeSecret = "ghp_0a1B2c3D4e5F6g7H8i9J0kLmNoPqRsTuVwXy";

    // Echtes gitleaks-`dir --report-format json`-Schema (Top-Level-Array; enthält bewusst Secret/Match).
    private static readonly string Json = $$"""
    [
      { "RuleID": "github-pat",
        "Description": "Uncovered a GitHub Personal Access Token.",
        "StartLine": 2, "EndLine": 2, "StartColumn": 23, "EndColumn": 63,
        "Match": "GhToken = \"{{FakeSecret}}\"",
        "Secret": "{{FakeSecret}}",
        "File": "config.cs", "Tags": [], "Fingerprint": "config.cs:github-pat:2" }
    ]
    """;

    [Fact]
    public async Task AnalyzeAsync_mapsGitleaksFinding_toSecretsFinding()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(1, Json, "")); // Exit 1 = Funde
        var analyzer = new GitleaksAnalyzer(runner, NullLogger<GitleaksAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        var findings = await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        var f = Assert.Single(findings);
        Assert.Equal("gitleaks", f.Tool);
        Assert.Equal(FindingCategory.Secrets, f.Category);
        Assert.Equal(FindingSeverity.High, f.Severity);
        Assert.Equal("config.cs", f.FilePath);
        Assert.Equal(2, f.Line);
        Assert.Equal("github-pat", f.RuleId);
        Assert.Contains("Personal Access Token", f.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_doesNotLeakTheSecretValue_intoTheFinding()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(1, Json, ""));
        var analyzer = new GitleaksAnalyzer(runner, NullLogger<GitleaksAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        var f = Assert.Single(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));

        // Der rohe Secret-/Match-Wert darf NIE im Fund landen (geht sonst in Prompt + Logs).
        Assert.DoesNotContain(FakeSecret, f.Message);
        Assert.DoesNotContain(FakeSecret, f.RuleId ?? "");
        Assert.DoesNotContain(FakeSecret, f.FilePath ?? "");
    }

    [Fact]
    public async Task AnalyzeAsync_invokesDir_withJsonReportToStdout()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, "[]", ""));
        var analyzer = new GitleaksAnalyzer(runner, NullLogger<GitleaksAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        var spec = runner.LastSpec!;
        Assert.Equal("gitleaks", spec.FileName);
        Assert.Equal("/tmp/x", spec.WorkingDirectory);
        Assert.Equal(
            new[] { "dir", ".", "--report-format", "json", "--report-path", "/dev/stdout", "--no-banner" },
            spec.Arguments);
    }

    [Fact]
    public async Task AnalyzeAsync_returnsEmpty_whenToolErrors()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(2, "", "gitleaks error")); // >1 = echter Fehler
        var analyzer = new GitleaksAnalyzer(runner, NullLogger<GitleaksAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
    }
}
