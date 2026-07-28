using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Dast;
using Naudit.Infrastructure.Docker;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class DastOrphanSweeperTests
{
    /// <summary>Stürzt Naudit mitten in einem DAST-Lauf ab, bleiben Container/Netz/Image stehen —
    /// beim nächsten Start müssen sie weg sein (fremde Container bleiben unangetastet).</summary>
    [Fact]
    public async Task Start_removesLeftoverDastResources_andLeavesForeignOnesAlone()
    {
        var docker = new FakeDockerClient
        {
            Containers =
            {
                ["naudit-dast-app-abc123"] = true, ["naudit-dast-pw-abc123"] = true,
                ["naudit-session-7"] = true, ["postgres"] = true,
            },
        };
        await docker.CreateNetworkAsync("naudit-dast-net-abc123");
        await docker.CreateNetworkAsync("bridge");
        docker.Images.Add("naudit-dast-img-abc123");
        docker.Images.Add("mcr.microsoft.com/playwright/mcp:latest");   // ProbeImage-Cache: bleibt
        var sweeper = new DastOrphanSweeper(docker, NullLogger<DastOrphanSweeper>.Instance);

        await sweeper.StartAsync(CancellationToken.None);

        Assert.Equal(["naudit-session-7", "postgres"], docker.Containers.Keys.Order());
        Assert.Equal(["bridge"], docker.Networks.ToList());
        Assert.Equal(["mcr.microsoft.com/playwright/mcp:latest"], docker.Images);
    }

    [Fact]
    public async Task Start_dockerUnavailable_doesNotThrow()
    {
        var sweeper = new DastOrphanSweeper(new ThrowingDocker(), NullLogger<DastOrphanSweeper>.Instance);

        await sweeper.StartAsync(CancellationToken.None); // fail-quiet: Host startet trotzdem
    }

    /// <summary>Ist der Socket nicht nutzbar (kein Mount / falsche group_add-GID ⇒ Ping false),
    /// wird der Sweep knapp und ohne Stacktrace übersprungen — kein Listing, kein Entfernen.</summary>
    [Fact]
    public async Task Start_socketUnreachable_skipsSweepWithoutTouchingResources()
    {
        var docker = new FakeDockerClient
        {
            PingResult = false,
            Containers = { ["naudit-dast-app-abc123"] = true },
        };
        var sweeper = new DastOrphanSweeper(docker, NullLogger<DastOrphanSweeper>.Instance);

        await sweeper.StartAsync(CancellationToken.None);

        Assert.Equal(["naudit-dast-app-abc123"], docker.Containers.Keys.Order()); // unangetastet
        Assert.DoesNotContain(docker.Calls, c => c.StartsWith("rm:", StringComparison.Ordinal));
    }

    /// <summary>Ein einzelner fehlschlagender Container-Abbau darf Netz- und Image-Aufräumen nicht
    /// verhindern — jeder Schritt ist einzeln fail-quiet (per-item, nicht der ganze Sweep).</summary>
    [Fact]
    public async Task Start_oneContainerRemovalFails_stillRemovesOtherContainerNetworkAndImage()
    {
        var docker = new PartlyFailingDocker("naudit-dast-app-abc123")
        {
            Containers =
            {
                ["naudit-dast-app-abc123"] = true, ["naudit-dast-pw-abc123"] = true,
            },
        };
        await docker.CreateNetworkAsync("naudit-dast-net-abc123");
        docker.Images.Add("naudit-dast-img-abc123");
        var sweeper = new DastOrphanSweeper(docker, NullLogger<DastOrphanSweeper>.Instance);

        await sweeper.StartAsync(CancellationToken.None);

        Assert.Equal(["naudit-dast-app-abc123"], docker.Containers.Keys.Order()); // blieb (Entfernen schlug fehl)
        Assert.Empty(docker.Networks);   // trotzdem abgeräumt
        Assert.Empty(docker.Images);     // trotzdem abgeräumt
    }

    private sealed class ThrowingDocker : FakeDockerClient
    {
        public override Task<IReadOnlyList<ContainerListEntry>> ListContainersAsync(
            string namePrefix, CancellationToken ct = default)
            => throw new Naudit.Infrastructure.Docker.DockerUnavailableException("fake: engine down");
    }

    private sealed class PartlyFailingDocker(string failingContainer) : FakeDockerClient
    {
        public override Task RemoveContainerAsync(string name, CancellationToken ct = default)
            => name == failingContainer
                ? throw new Naudit.Infrastructure.Docker.DockerUnavailableException("fake: remove failed")
                : base.RemoveContainerAsync(name, ct);
    }
}
