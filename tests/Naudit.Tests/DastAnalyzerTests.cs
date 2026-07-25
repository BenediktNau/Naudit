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

    [Fact]
    public async Task Analyze_appNeverStarts_returnsEmpty()   // runner liefert null
    {
        var app = new FakeAppRunner { ReturnNull = true };
        var analyzer = new DastAnalyzer(app, Options(), new FakeChatClient("{\"findings\":[]}"),
            new FakeDockerClient(), NullLoggerFactory.Instance, probeToolsOverride: []);

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
        Assert.True(app.RunCalled);
    }

    [Fact]
    public async Task Analyze_nonJsonModelOutput_returnsEmpty_andTearsDown()
    {
        var app = new FakeAppRunner();
        var analyzer = new DastAnalyzer(app, Options(), new FakeChatClient("I could not access the app."),
            new FakeDockerClient(), NullLoggerFactory.Instance, probeToolsOverride: []);

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
        Assert.True(app.Disposed);
    }

    [Fact]
    public async Task Analyze_probeSessionThrows_returnsEmpty_andTearsDownApp()
    {
        var app = new FakeAppRunner();
        // probeToolsOverride null ⇒ echter DastProbeSession.StartAsync gegen den leeren FakeDockerClient ⇒ wirft
        // (kurzer HandshakeTimeout, damit der Test schnell bleibt statt die Default-10s abzuwarten)
        var opts = Options(); opts.HandshakeTimeout = TimeSpan.FromMilliseconds(200);
        var analyzer = new DastAnalyzer(app, opts, new FakeChatClient("{\"findings\":[]}"),
            new FakeDockerClient(), NullLoggerFactory.Instance, probeToolsOverride: null);

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
        Assert.True(app.Disposed);   // App-Teardown trotz Probe-Fehler garantiert
    }

    [Fact]
    public async Task Analyze_incompleteFindings_areRejected()
    {
        var app = new FakeAppRunner();
        // Leerer Eintrag + Eintrag ohne Summary werden verworfen; nur der vollständige überlebt
        // (kein " ()"-Durchrutscher).
        var chat = new FakeChatClient(
            "{\"findings\":[{},{\"severity\":\"low\",\"endpoint\":\"/x\"}," +
            "{\"severity\":\"medium\",\"endpoint\":\"/y\",\"summary\":\"Real\"}]}");
        var analyzer = new DastAnalyzer(app, Options(), chat, new FakeDockerClient(),
            NullLoggerFactory.Instance, probeToolsOverride: []);

        var f = Assert.Single(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
        Assert.Equal(FindingSeverity.Medium, f.Severity);
        Assert.Contains("Real", f.Message);
    }

    [Fact]
    public async Task Analyze_criticalSeverity_mapsToHigh()
    {
        var app = new FakeAppRunner();
        var chat = new FakeChatClient(
            "{\"findings\":[{\"severity\":\"critical\",\"endpoint\":\"/admin\",\"summary\":\"Auth bypass\"}]}");
        var analyzer = new DastAnalyzer(app, Options(), chat, new FakeDockerClient(),
            NullLoggerFactory.Instance, probeToolsOverride: []);

        var f = Assert.Single(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
        Assert.Equal(FindingSeverity.High, f.Severity);   // nicht still auf Low herabgestuft
    }

    [Fact]
    public async Task Analyze_callerCancelled_propagates()
    {
        var app = new FakeAppRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var analyzer = new DastAnalyzer(app, Options(), new FakeChatClient("{\"findings\":[]}"),
            new FakeDockerClient(), NullLoggerFactory.Instance, probeToolsOverride: []);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync(new Ws("/tmp/x"), [], cts.Token));
    }
}
