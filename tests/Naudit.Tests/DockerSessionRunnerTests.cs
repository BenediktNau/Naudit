using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Infrastructure.Docker;
using Naudit.Infrastructure.Process;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class DockerSessionRunnerTests
{
    private static ProcessSpec Spec(string? stdIn = "DIFF", TimeSpan? timeout = null) => new(
        FileName: "claude",
        Arguments: ["-p", "--output-format", "json"],
        StdIn: stdIn,
        Environment: new Dictionary<string, string?>
        {
            ["CLAUDE_CONFIG_DIR"] = "/tmp/host-only",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "tok-123",
        },
        WorkingDirectory: "/tmp",
        Timeout: timeout ?? TimeSpan.FromMinutes(5));

    private static (DockerSessionRunner Runner, FakeDockerClient Docker, StubProcessRunner Fallback, SessionSandboxState State, SessionContainerManager Manager)
        Create(FakeDockerClient? docker = null)
    {
        docker ??= new FakeDockerClient();
        var manager = new SessionContainerManager(docker, new SessionSandboxOptions(),
            NullLogger<SessionContainerManager>.Instance, new FakeTime());
        var fallback = new StubProcessRunner(_ => new ProcessResult(0, "fallback-out", ""));
        var state = new SessionSandboxState();
        var runner = new DockerSessionRunner(42, manager, docker, fallback, state,
            NullLogger<DockerSessionRunner>.Instance);
        return (runner, docker, fallback, state, manager);
    }

    [Fact]
    public async Task Run_execsInContainer_withShRedirect_andTokenOnlyEnv()
    {
        var (runner, docker, fallback, _, _) = Create();
        docker.ExecResults.Enqueue(new DockerExecResult(0, "{\"ok\":true}", ""));

        var result = await runner.RunAsync(Spec());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("{\"ok\":true}", result.StdOut);
        Assert.Empty(fallback.Specs); // kein In-Process-Lauf

        var written = Assert.Single(docker.WrittenFiles);
        Assert.Equal(("naudit-session-42", "/tmp/naudit-stdin", "DIFF"), written);

        var exec = Assert.Single(docker.Execs);
        Assert.Equal("naudit-session-42", exec.Container);
        Assert.Equal("/tmp", exec.WorkingDir);
        Assert.Equal(["/bin/sh", "-c", "exec \"$0\" \"$@\" < /tmp/naudit-stdin", "claude", "-p", "--output-format", "json"],
            exec.Argv);
        // Env-Filter: NUR der Token wandert mit, CLAUDE_CONFIG_DIR wird verworfen (Volume-HOME gewinnt).
        var env = Assert.Single(exec.Env!);
        Assert.Equal(("CLAUDE_CODE_OAUTH_TOKEN", "tok-123"), (env.Key, env.Value));
    }

    [Fact]
    public async Task Run_withoutStdin_execsArgvDirectly()
    {
        var (runner, docker, _, _, _) = Create();

        await runner.RunAsync(Spec(stdIn: null));

        Assert.Empty(docker.WrittenFiles);
        Assert.Equal(["claude", "-p", "--output-format", "json"], Assert.Single(docker.Execs).Argv);
    }

    [Fact]
    public async Task Run_containerVanishedMidExec_ensuresAndRetriesOnce()
    {
        var (runner, docker, fallback, _, _) = Create();
        docker.FailNextExecs = 1; // erster Exec scheitert (Container extern entfernt)
        docker.ExecResults.Enqueue(new DockerExecResult(0, "second-try", ""));

        var result = await runner.RunAsync(Spec());

        Assert.Equal("second-try", result.StdOut);
        Assert.Equal(2, docker.Execs.Count);
        Assert.Empty(fallback.Specs);
    }

    [Fact]
    public async Task Run_dockerBroken_fallsBackInProcess()
    {
        var (runner, docker, fallback, _, _) = Create();
        docker.FailNextExecs = 2; // Erstversuch UND Retry scheitern

        var result = await runner.RunAsync(Spec());

        Assert.Equal("fallback-out", result.StdOut);
        Assert.Single(fallback.Specs); // Original-Spec ging in-process weiter
    }

    [Fact]
    public async Task Run_knownUnreachable_skipsDockerEntirely()
    {
        var (runner, docker, fallback, state, _) = Create();
        state.ReportPing(false);

        var result = await runner.RunAsync(Spec());

        Assert.Equal("fallback-out", result.StdOut);
        Assert.Empty(docker.Execs);
        Assert.Empty(docker.Calls); // nicht mal ein Inspect
    }

    [Fact]
    public async Task Run_timeout_stopsContainer_andThrowsTimeout()
    {
        var (runner, docker, _, _, _) = Create();
        docker.ExecDelay = TimeSpan.FromSeconds(30);

        await Assert.ThrowsAsync<TimeoutException>(
            () => runner.RunAsync(Spec(timeout: TimeSpan.FromMilliseconds(100))));

        Assert.Contains("stop:naudit-session-42", docker.Calls); // Kill-Pfad: Stop beendet den Exec
    }

    [Fact]
    public async Task Run_externalCancel_rethrowsCancellation()
    {
        var (runner, docker, _, _, _) = Create();
        docker.ExecDelay = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(Spec(), cts.Token));
    }

    [Fact]
    public async Task Run_success_touchesLastUsed()
    {
        var (runner, _, _, _, manager) = Create();

        await runner.RunAsync(Spec());

        Assert.NotNull(manager.LastUsed(42));
    }
}
