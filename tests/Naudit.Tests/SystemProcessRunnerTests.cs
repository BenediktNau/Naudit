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

    [Fact]
    public async Task RunAsync_defaultSpec_inheritsHostEnvironment()
    {
        if (!Posix) return;
        Environment.SetEnvironmentVariable("NAUDIT_TEST_INHERIT_PROBE", "geerbt");
        try
        {
            var spec = new ProcessSpec("env", Array.Empty<string>(), StdIn: null,
                Environment: null, WorkingDirectory: null, Timeout: TimeSpan.FromSeconds(10));

            var result = await new SystemProcessRunner().RunAsync(spec);

            Assert.Contains("NAUDIT_TEST_INHERIT_PROBE=geerbt", result.StdOut);
        }
        finally { Environment.SetEnvironmentVariable("NAUDIT_TEST_INHERIT_PROBE", null); }
    }

    [Fact]
    public async Task RunAsync_inheritedEnvironmentAllowlist_dropsEverythingElse_butKeepsExplicitEnv()
    {
        if (!Posix) return;
        Environment.SetEnvironmentVariable("Naudit__GitHub__Token", "geheim");
        Environment.SetEnvironmentVariable("TRIVY_TEST_PROBE", "durch");
        try
        {
            var spec = new ProcessSpec("env", Array.Empty<string>(), StdIn: null,
                Environment: new Dictionary<string, string?> { ["EXTRA"] = "1" },
                WorkingDirectory: null, Timeout: TimeSpan.FromSeconds(10),
                InheritedEnvironment: [.. ProcessEnvironment.Minimal, "TRIVY_*"]);

            var result = await new SystemProcessRunner().RunAsync(spec);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("geheim", result.StdOut);
            Assert.Contains("TRIVY_TEST_PROBE=durch", result.StdOut);   // Präfix-Eintrag
            Assert.Contains("EXTRA=1", result.StdOut);                  // explizite Env bleibt additiv
            Assert.Contains("PATH=", result.StdOut);                    // Basis bleibt erhalten
        }
        finally
        {
            Environment.SetEnvironmentVariable("Naudit__GitHub__Token", null);
            Environment.SetEnvironmentVariable("TRIVY_TEST_PROBE", null);
        }
    }
}
