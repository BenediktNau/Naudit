using Naudit.Infrastructure.Process;
using Xunit;

namespace Naudit.Tests;

public class SystemProcessRunnerTests
{
    // POSIX-only: nutzt `cat`/`sleep`. Auf Nicht-POSIX still überspringen.
    private static bool Posix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    [Fact]
    public async Task RunAsync_pipesStdInToStdOut_andReportsExitZero()
    {
        if (!Posix) return;
        var runner = new SystemProcessRunner();
        var spec = new ProcessSpec("cat", Array.Empty<string>(), "hallo welt",
            Environment: null, WorkingDirectory: null, Timeout: TimeSpan.FromSeconds(10));

        var result = await runner.RunAsync(spec);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hallo welt", result.StdOut);
    }

    [Fact]
    public async Task RunAsync_killsOnTimeout_andThrowsTimeout()
    {
        if (!Posix) return;
        var runner = new SystemProcessRunner();
        var spec = new ProcessSpec("sleep", new[] { "5" }, StdIn: null,
            Environment: null, WorkingDirectory: null, Timeout: TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(spec));
    }

    [Fact]
    public async Task RunAsync_externalCancellation_killsProcess_andThrowsOperationCanceled()
    {
        if (!Posix) return;
        var runner = new SystemProcessRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // bereits abgebrochen → der Cancel erreicht den Kill-Pfad, kein verwaister Prozess
        var spec = new ProcessSpec("sleep", new[] { "5" }, StdIn: null,
            Environment: null, WorkingDirectory: null, Timeout: TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(spec, cts.Token));
    }

    [Fact]
    public async Task RunAsync_missingBinary_throwsInvalidOperation()
    {
        var runner = new SystemProcessRunner();
        var spec = new ProcessSpec("naudit-no-such-binary-xyz", Array.Empty<string>(), StdIn: null,
            Environment: null, WorkingDirectory: null, Timeout: TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(spec));
    }
}
