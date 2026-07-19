using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class SandboxSweeperServiceTests
{
    private static (SandboxSweeperService Sweeper, FakeDockerClient Docker, SessionContainerManager Manager, SessionSandboxState State, FakeTime Time)
        Create(FakeDockerClient? docker = null, SessionSandboxOptions? options = null)
    {
        docker ??= new FakeDockerClient();
        var time = new FakeTime();
        var manager = new SessionContainerManager(docker, options ?? new SessionSandboxOptions(),
            NullLogger<SessionContainerManager>.Instance, time);
        var state = new SessionSandboxState();
        var sweeper = new SandboxSweeperService(docker, manager, state,
            NullLogger<SandboxSweeperService>.Instance);
        return (sweeper, docker, manager, state, time);
    }

    [Fact]
    public async Task Adopt_pingFails_setsStateFalse_andSkipsAdoption()
    {
        var docker = new FakeDockerClient { PingResult = false, Containers = { ["naudit-session-1"] = true } };
        var (sweeper, _, manager, state, _) = Create(docker);

        await sweeper.AdoptAsync(CancellationToken.None);

        Assert.False(state.SocketReachable);
        Assert.Null(manager.LastUsed(1)); // keine Adoption ohne erreichbaren Socket
    }

    [Fact]
    public async Task Adopt_pingOk_adoptsExistingContainers()
    {
        var docker = new FakeDockerClient { Containers = { ["naudit-session-8"] = true } };
        var (sweeper, _, manager, state, _) = Create(docker);

        await sweeper.AdoptAsync(CancellationToken.None);

        Assert.True(state.SocketReachable);
        Assert.NotNull(manager.LastUsed(8));
    }

    [Fact]
    public async Task Tick_sweepsIdleContainers()
    {
        var (sweeper, docker, manager, _, time) = Create(
            options: new SessionSandboxOptions { IdleTimeout = TimeSpan.FromHours(1) });
        await manager.EnsureRunningAsync(1);
        time.UtcNow = time.UtcNow.AddHours(2);

        await sweeper.TickAsync(CancellationToken.None);

        Assert.Contains("stop:naudit-session-1", docker.Calls);
    }

    [Fact]
    public async Task Tick_pingRecovers_updatesState_beforeSweeping()
    {
        var (sweeper, docker, _, state, _) = Create();
        state.ReportPing(false);
        docker.PingResult = true;

        await sweeper.TickAsync(CancellationToken.None);

        Assert.True(state.SocketReachable); // Selbstheilung: Runner nutzt Docker wieder
    }

    [Fact]
    public async Task Tick_pingDown_setsStateFalse_andSkipsSweep()
    {
        var docker = new FakeDockerClient { PingResult = false, Containers = { ["naudit-session-1"] = true } };
        var (sweeper, _, _, state, _) = Create(docker);

        await sweeper.TickAsync(CancellationToken.None);

        Assert.False(state.SocketReachable);
        Assert.DoesNotContain(docker.Calls, c => c.StartsWith("stop:"));
    }
}
