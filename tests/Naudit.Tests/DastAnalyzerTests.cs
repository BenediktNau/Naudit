using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Dast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class DastAnalyzerTests
{
    private const string Project = "acme/shop";

    private sealed class Ws(string root) : IReviewWorkspace
    {
        public string RootPath => root;
        public string ProjectId => Project;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DastOptions Options() => new() { Enabled = true, Projects = { Project } };

    [Fact]
    public async Task Analyze_mapsModelJson_toDastFindings()
    {
        var app = new FakeAppRunner(); // RunAsync -> non-null RunningApp
        var chat = new FakeChatClient(
            "{\"findings\":[{\"severity\":\"High\",\"endpoint\":\"/search?q=\",\"summary\":\"Reflected XSS\"}]}");
        var analyzer = new DastAnalyzer(app, Options(), chat, new FakeDockerClient(),
            NullLoggerFactory.Instance, probeToolsOverride: []);   // leere Toolliste ⇒ kein echter MCP

        var findings = await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        var f = Assert.Single(findings);
        Assert.Equal("dast", f.Tool);
        Assert.Equal(FindingCategory.Dast, f.Category);
        Assert.Equal(FindingSeverity.High, f.Severity);
        Assert.Contains("/search?q=", f.Message);
        Assert.Contains("Reflected XSS", f.Message);
        Assert.Null(f.FilePath);
        Assert.True(app.Disposed);   // Teardown lief
    }

    [Fact]
    public async Task Analyze_notAllowlisted_returnsEmpty_withoutRunning()
    {
        var app = new FakeAppRunner();
        var opts = Options(); opts.Projects.Clear();
        var analyzer = new DastAnalyzer(app, opts, new FakeChatClient("{\"findings\":[]}"),
            new FakeDockerClient(), NullLoggerFactory.Instance, probeToolsOverride: []);

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
        Assert.False(app.RunCalled);
    }
}
