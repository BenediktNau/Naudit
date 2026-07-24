using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure.Dast;
using Naudit.Infrastructure.Docker;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class DockerAppRunnerTests
{
    private const string Project = "acme/shop";

    private sealed class Ws(string root) : IReviewWorkspace
    {
        public string RootPath => root;
        public string ProjectId => Project;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Checkout mit Dockerfile; Rückgabe ist der Root-Pfad.</summary>
    private static IReviewWorkspace Checkout(bool withDockerfile = true)
    {
        var root = Path.Combine(Path.GetTempPath(), $"naudit-dast-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        if (withDockerfile)
            File.WriteAllText(Path.Combine(root, "Dockerfile"), "FROM scratch\n");
        return new Ws(root);
    }

    private static DastOptions Options() => new()
    {
        Enabled = true,
        Projects = { Project },
        HealthPollInterval = TimeSpan.FromMilliseconds(1),
        TimeBudget = TimeSpan.FromSeconds(5),
    };

    private static (DockerAppRunner Runner, FakeDockerClient Docker) Create(
        DastOptions? options = null, FakeDockerClient? docker = null)
    {
        docker ??= new FakeDockerClient();
        return (new DockerAppRunner(docker, options ?? Options(),
            NullLogger<DockerAppRunner>.Instance), docker);
    }

    [Fact]
    public async Task Run_buildsRunsAndReturnsInternalUrl_reachedOnlyViaExec()
    {
        var (runner, docker) = Create();

        await using var app = await runner.RunAsync(Checkout());

        Assert.NotNull(app);
        Assert.StartsWith("naudit-dast-net-", app!.NetworkName);
        Assert.StartsWith("naudit-dast-app-", app.ContainerName);
        Assert.StartsWith("naudit-dast-pw-", app.ProbeContainerName);
        Assert.Equal($"http://{app.ContainerName}:8080/", app.InternalUrl);

        // Reihenfolge: bauen, Netz anlegen, Probe-Image pullen, App- und Probe-Container starten.
        var relevant = docker.Calls.Where(c =>
            c.StartsWith("build:") || c.StartsWith("netcreate:") || c.StartsWith("pull:") || c.StartsWith("run:"))
            .Select(c => c.Split(':')[0]).ToList();
        Assert.Equal(["build", "netcreate", "pull", "run", "run"], relevant);

        // Healthcheck als exec im Probe-Container gegen die interne URL — Naudit betritt das Netz nie.
        var exec = Assert.Single(docker.Execs);
        Assert.Equal(app.ProbeContainerName, exec.Container);
        Assert.Contains(app.InternalUrl, exec.Argv);

        var build = Assert.Single(docker.Builds);
        Assert.Equal("Dockerfile", build.Dockerfile);
        Assert.True(build.ContextBytes > 0);
    }

    [Fact]
    public async Task Run_startsBothContainers_withLimits_andWithoutVolumeOrEnvironment()
    {
        var (runner, docker) = Create();

        await using var app = await runner.RunAsync(Checkout());

        var appSpec = docker.RunSpecs.Single(s => s.Name.StartsWith("naudit-dast-app-", StringComparison.Ordinal));
        Assert.Equal(app!.NetworkName, appSpec.Network);
        Assert.Null(appSpec.VolumeName);       // kein Volume: der Container darf nichts überdauern
        Assert.Null(appSpec.Environment);      // niemals Naudit-Secrets im getesteten Container
        Assert.Empty(appSpec.Command);         // CMD/ENTRYPOINT des gebauten Images gilt
        Assert.Equal(1024, appSpec.Limits!.MemoryMb);
        Assert.Equal(256, appSpec.Limits.PidsLimit);

        var probeSpec = docker.RunSpecs.Single(s => s.Name.StartsWith("naudit-dast-pw-", StringComparison.Ordinal));
        Assert.Equal(app.NetworkName, probeSpec.Network);
        Assert.Equal(["sleep", "infinity"], probeSpec.Entrypoint);   // lebt passiv, wird nur per exec benutzt
        Assert.NotNull(probeSpec.Limits);      // auch der Browser-Container bleibt gedeckelt
    }

    [Fact]
    public async Task Run_projectNotOnAllowlist_returnsNull_withoutTouchingDocker()
    {
        var options = Options();
        options.Projects.Clear();
        options.Projects.Add("someone/else");
        var (runner, docker) = Create(options);

        Assert.Null(await runner.RunAsync(Checkout()));
        Assert.Empty(docker.Calls);
    }

    /// <summary>Bricht der AUFRUFER während des Health-Poll-Delays ab, muss die Cancellation
    /// propagieren (nicht als "App nicht erreichbar" ⇒ null enden) — und der Teardown gelaufen sein.</summary>
    [Fact]
    public async Task Run_callerCancelledDuringHealthPoll_throws_andTearsDown()
    {
        var options = Options();
        options.HealthPollInterval = TimeSpan.FromSeconds(5);   // Cancel landet sicher im Delay
        options.TimeBudget = TimeSpan.FromSeconds(30);
        var docker = new FakeDockerClient();
        docker.ExecResults.Enqueue(new DockerExecResult(1, "", ""));  // erster Probe-Versuch: noch nicht healthy
        var (runner, _) = Create(options, docker);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(Checkout(), cts.Token));

        Assert.Empty(docker.Containers);
        Assert.Empty(docker.Networks);
        Assert.DoesNotContain(docker.Images, i => i.StartsWith("naudit-dast-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_withoutDockerfile_returnsNull_withoutTouchingDocker()
    {
        var (runner, docker) = Create();

        Assert.Null(await runner.RunAsync(Checkout(withDockerfile: false)));
        Assert.Empty(docker.Calls);
    }

    [Fact]
    public async Task Run_buildFails_returnsNull_andLeavesNothingBehind()
    {
        var docker = new FakeDockerClient { NextBuildFails = true };
        var (runner, _) = Create(docker: docker);

        Assert.Null(await runner.RunAsync(Checkout()));

        Assert.Empty(docker.Images);
        Assert.Empty(docker.Networks);
        Assert.Empty(docker.Containers);
    }

    /// <summary>App kommt nie hoch (Exec-Probe liefert dauerhaft Exit 1): Zeitbudget greift, danach
    /// ist die gesamte Review-Topologie wieder weg — nur das gecachte ProbeImage bleibt.</summary>
    [Fact]
    public async Task Run_appNeverBecomesHealthy_returnsNull_andTearsDownEverything()
    {
        var options = Options();
        options.TimeBudget = TimeSpan.FromMilliseconds(150);
        var docker = new FakeDockerClient { DefaultExecResult = new DockerExecResult(1, "", "") };
        var (runner, _) = Create(options, docker);

        Assert.Null(await runner.RunAsync(Checkout()));

        Assert.Empty(docker.Containers);
        Assert.Empty(docker.Networks);
        Assert.Equal([options.ProbeImage], docker.Images);   // Cache bleibt, naudit-dast-img-* ist weg
    }

    [Fact]
    public async Task Run_dockerUnavailableMidway_returnsNull_andTriesTeardownAnyway()
    {
        var docker = new ThrowOnNetworkCreate();
        var (runner, _) = Create(docker: docker);

        Assert.Null(await runner.RunAsync(Checkout()));

        Assert.Contains(docker.Calls, c => c.StartsWith("rmimg:"));
    }

    /// <summary>Erfolgsfall: Dispose räumt ab — und ein zweites Dispose räumt nicht erneut ab.</summary>
    [Fact]
    public async Task Dispose_tearsDownOnce_andIsIdempotent()
    {
        var (runner, docker) = Create();
        var app = await runner.RunAsync(Checkout());

        await app!.DisposeAsync();
        var afterFirst = docker.Calls.Count(c => c.StartsWith("rm:"));
        await app.DisposeAsync();

        Assert.Equal(2, afterFirst);   // App- UND Probe-Container, je genau einmal
        Assert.Equal(afterFirst, docker.Calls.Count(c => c.StartsWith("rm:")));
        Assert.Empty(docker.Containers);
        Assert.Empty(docker.Networks);
        Assert.DoesNotContain(docker.Images, i => i.StartsWith("naudit-dast-", StringComparison.Ordinal));
    }

    /// <summary>FakeDockerClient, der beim Netz-Anlegen wie eine tote Engine reagiert.</summary>
    private sealed class ThrowOnNetworkCreate : FakeDockerClient
    {
        public override Task CreateNetworkAsync(string name, CancellationToken ct = default)
            => throw new Naudit.Infrastructure.Docker.DockerUnavailableException("fake: engine down");
    }
}
