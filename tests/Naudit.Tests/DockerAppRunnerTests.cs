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
}
