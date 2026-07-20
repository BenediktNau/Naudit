using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class SessionContainerManagerTests
{
    private static SessionContainerManager Create(FakeDockerClient docker, FakeTime? time = null,
        SessionSandboxOptions? options = null)
        => new(docker, options ?? new SessionSandboxOptions(),
            NullLogger<SessionContainerManager>.Instance, time ?? new FakeTime());

    [Fact]
    public async Task EnsureRunning_missing_runsDetached_withVolumeAndSleep()
    {
        var docker = new FakeDockerClient();
        var manager = Create(docker);

        var name = await manager.EnsureRunningAsync(7);

        Assert.Equal("naudit-session-7", name);
        Assert.Equal(["run:naudit-session-7"], docker.Calls.Where(c => c.StartsWith("run:")));
        Assert.Equal("sha256:self-image", docker.LastRunSpec!.Image);
        Assert.Equal("naudit-session-7", docker.LastRunSpec.VolumeName);
        Assert.Equal("/home/app", docker.LastRunSpec.VolumeTarget);
        Assert.Equal(["sleep", "infinity"], docker.LastRunSpec.Command);
        Assert.NotNull(manager.LastUsed(7)); // EnsureRunning zählt als Nutzung
    }

    [Fact]
    public async Task EnsureRunning_imageOverride_skipsSelfInspection()
    {
        var docker = new FakeDockerClient { SelfImage = null };
        var manager = Create(docker, options: new SessionSandboxOptions { Image = "ghcr.io/x/naudit:v9" });

        await manager.EnsureRunningAsync(1);

        Assert.Equal("ghcr.io/x/naudit:v9", docker.LastRunSpec!.Image);
        Assert.DoesNotContain(docker.Calls, c => c.StartsWith("inspect-self:"));
    }

    [Fact]
    public async Task EnsureRunning_noImageResolvable_throwsDockerUnavailable()
    {
        var docker = new FakeDockerClient { SelfImage = null }; // nicht im Container, kein Override
        var manager = Create(docker);

        await Assert.ThrowsAsync<Naudit.Infrastructure.Docker.DockerUnavailableException>(
            () => manager.EnsureRunningAsync(1));
    }

    [Fact]
    public async Task EnsureRunning_exited_startsWithoutCreate()
    {
        var docker = new FakeDockerClient { Containers = { ["naudit-session-3"] = false } };
        var manager = Create(docker);

        await manager.EnsureRunningAsync(3);

        Assert.Contains("start:naudit-session-3", docker.Calls);
        Assert.DoesNotContain(docker.Calls, c => c.StartsWith("run:"));
    }

    [Fact]
    public async Task EnsureRunning_running_isNoop()
    {
        var docker = new FakeDockerClient { Containers = { ["naudit-session-3"] = true } };
        var manager = Create(docker);

        await manager.EnsureRunningAsync(3);

        Assert.DoesNotContain(docker.Calls, c => c.StartsWith("run:") || c.StartsWith("start:"));
    }

    [Fact]
    public async Task EnsureRunning_capReached_stopsLeastRecentlyUsed()
    {
        var docker = new FakeDockerClient();
        var time = new FakeTime();
        var manager = Create(docker, time, new SessionSandboxOptions { MaxLiveContainers = 2 });

        await manager.EnsureRunningAsync(1);
        time.UtcNow = time.UtcNow.AddMinutes(1);
        await manager.EnsureRunningAsync(2);
        time.UtcNow = time.UtcNow.AddMinutes(1);
        await manager.EnsureRunningAsync(3); // Cap 2 erreicht ⇒ LRU (Konto 1) wird gestoppt

        Assert.Contains("stop:naudit-session-1", docker.Calls);
        Assert.False(docker.Containers["naudit-session-1"]);
        Assert.True(docker.Containers["naudit-session-2"]);
        Assert.True(docker.Containers["naudit-session-3"]);
    }

    [Fact]
    public async Task AcquireLock_serializesPerAccount_butNotAcrossAccounts()
    {
        var docker = new FakeDockerClient();
        var manager = Create(docker);

        var first = await manager.AcquireLockAsync(5);
        var secondTask = manager.AcquireLockAsync(5);
        var otherAccount = await manager.AcquireLockAsync(6); // fremdes Konto blockiert nicht

        Assert.False(secondTask.IsCompleted);
        first.Dispose();
        (await secondTask).Dispose();
        otherAccount.Dispose();
    }

    [Fact]
    public async Task Sweep_stopsOnlyIdleRunningContainers()
    {
        var docker = new FakeDockerClient();
        var time = new FakeTime();
        var manager = Create(docker, time, new SessionSandboxOptions { IdleTimeout = TimeSpan.FromHours(1) });

        await manager.EnsureRunningAsync(1);
        time.UtcNow = time.UtcNow.AddMinutes(90); // Konto 1 ist jetzt idle
        await manager.EnsureRunningAsync(2);       // Konto 2 frisch

        await manager.SweepIdleAsync();

        Assert.Contains("stop:naudit-session-1", docker.Calls);
        Assert.DoesNotContain("stop:naudit-session-2", docker.Calls);
    }

    [Fact]
    public async Task Adopt_registersOnlyParsablePrefixedNames()
    {
        var docker = new FakeDockerClient
        {
            Containers =
            {
                ["naudit-session-9"] = true,
                ["naudit-session-x"] = true,  // nicht parsebar ⇒ ignoriert
                ["someone-elses-container"] = true,
            },
        };
        var manager = Create(docker);

        await manager.AdoptExistingAsync();

        Assert.NotNull(manager.LastUsed(9));
        // Frisch adoptiert zählt als "gerade genutzt": ein sofortiger Sweep stoppt nichts.
        await manager.SweepIdleAsync();
        Assert.DoesNotContain(docker.Calls, c => c.StartsWith("stop:"));
    }

    [Fact]
    public async Task Remove_stopsRemovesContainerAndVolume()
    {
        var docker = new FakeDockerClient { Containers = { ["naudit-session-4"] = true } };
        docker.Volumes.Add("naudit-session-4");
        var manager = Create(docker);

        Assert.True(await manager.RemoveAsync(4));

        Assert.Equal(["stop:naudit-session-4", "rm:naudit-session-4", "rmvol:naudit-session-4"],
            docker.Calls.Where(c => !c.StartsWith("ping")));
        Assert.Empty(docker.Containers);
        Assert.Empty(docker.Volumes);
        Assert.Null(manager.LastUsed(4));
    }

    /// <summary>Der Aufruf hängt am HTTP-Request (Token löschen/Pool-Austritt/Suspend): er darf nicht
    /// auf einen laufenden Review-Exec warten. Nach RemoveTimeout gibt er auf — aufgeräumt wird dann
    /// verzögert von der Reconciliation im Sweeper.</summary>
    [Fact]
    public async Task Remove_execInFlight_givesUpAfterTimeout_withoutTouchingContainer()
    {
        var docker = new FakeDockerClient { Containers = { ["naudit-session-4"] = true } };
        docker.Volumes.Add("naudit-session-4");
        var manager = Create(docker, options: new SessionSandboxOptions
        {
            RemoveTimeout = TimeSpan.FromMilliseconds(50),
        });
        using var execLock = await manager.AcquireLockAsync(4);

        Assert.False(await manager.RemoveAsync(4));

        Assert.DoesNotContain(docker.Calls, c => c.StartsWith("stop:") || c.StartsWith("rm"));
        Assert.True(docker.Containers["naudit-session-4"]);
        Assert.Contains("naudit-session-4", docker.Volumes);
    }

    [Fact]
    public async Task CountRunning_countsOnlyRunningPrefixed()
    {
        var docker = new FakeDockerClient
        {
            Containers = { ["naudit-session-1"] = true, ["naudit-session-2"] = false, ["other"] = true },
        };
        var manager = Create(docker);

        Assert.Equal(1, await manager.CountRunningAsync());
    }

    [Fact]
    public async Task Sweep_skipsContainerWithInFlightExecLock()
    {
        var docker = new FakeDockerClient();
        var time = new FakeTime();
        var manager = Create(docker, time, new SessionSandboxOptions { IdleTimeout = TimeSpan.FromHours(1) });

        await manager.EnsureRunningAsync(1);
        time.UtcNow = time.UtcNow.AddHours(2); // Konto 1 ist jetzt idle

        var inFlight = await manager.AcquireLockAsync(1); // simuliert einen laufenden Exec
        await manager.SweepIdleAsync();
        Assert.DoesNotContain("stop:naudit-session-1", docker.Calls);
        Assert.True(docker.Containers["naudit-session-1"]);

        inFlight.Dispose();
        await manager.SweepIdleAsync(); // kein Exec mehr in Flight ⇒ jetzt darf gestoppt werden
        Assert.Contains("stop:naudit-session-1", docker.Calls);
    }

    [Fact]
    public async Task EnforceCap_skipsLockedAccount_stopsNextLru()
    {
        var docker = new FakeDockerClient();
        var time = new FakeTime();
        var manager = Create(docker, time, new SessionSandboxOptions { MaxLiveContainers = 2 });

        await manager.EnsureRunningAsync(1); // ältester
        time.UtcNow = time.UtcNow.AddMinutes(1);
        await manager.EnsureRunningAsync(2);
        time.UtcNow = time.UtcNow.AddMinutes(1);

        var inFlight = await manager.AcquireLockAsync(1); // Konto 1 (LRU-Kandidat) hat einen Exec in Flight

        await manager.EnsureRunningAsync(3); // Cap 2 erreicht ⇒ Konto 1 überspringen, Konto 2 stoppen

        Assert.Contains("stop:naudit-session-2", docker.Calls);
        Assert.DoesNotContain("stop:naudit-session-1", docker.Calls);
        Assert.True(docker.Containers["naudit-session-1"]);

        inFlight.Dispose();
    }

    [Fact]
    public async Task EnsureRunning_concurrentCreates_respectCap()
    {
        var docker = new FakeDockerClient { RunDelay = TimeSpan.FromMilliseconds(50) };
        var manager = Create(docker, options: new SessionSandboxOptions { MaxLiveContainers = 1 });

        await Task.WhenAll(manager.EnsureRunningAsync(1), manager.EnsureRunningAsync(2));

        Assert.Equal(1, await manager.CountRunningAsync());
    }
}
