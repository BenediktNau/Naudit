using Naudit.Infrastructure.Docker;
using Xunit;

namespace Naudit.Tests;

/// <summary>Opt-in-Integrationstest gegen ein echtes Docker (Muster NauditDbContextPostgresTests):
/// läuft nur mit NAUDIT_TEST_DOCKER=1 (und lokal vorhandenem Image, Default busybox:latest —
/// vorher `docker pull busybox` ausführen). Lokal:
///   NAUDIT_TEST_DOCKER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SocketDockerClientTests
/// </summary>
public class SocketDockerClientTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("NAUDIT_TEST_DOCKER") == "1";
    private static string Image => Environment.GetEnvironmentVariable("NAUDIT_TEST_DOCKER_IMAGE") ?? "busybox:latest";
    private static string SocketPath => Environment.GetEnvironmentVariable("NAUDIT_TEST_DOCKER_SOCKET") ?? "/var/run/docker.sock";

    [Fact]
    public async Task FullLifecycle_run_exec_stop_start_remove()
    {
        if (!Enabled) return; // ohne Docker-Env: übersprungen

        using var docker = new SocketDockerClient(SocketPath);
        Assert.True(await docker.PingAsync());

        var name = $"naudit-test-{Guid.NewGuid():N}";
        try
        {
            Assert.Null(await docker.InspectContainerAsync(name));

            await docker.RunDetachedAsync(new ContainerRunSpec(name, Image, name, "/data", ["sleep", "300"]));
            Assert.True((await docker.InspectContainerAsync(name))!.Running);

            await docker.WriteFileAsync(name, "/tmp", "stdin-test", "hello sandbox");
            var cat = await docker.ExecAsync(name,
                ["/bin/sh", "-c", "exec \"$0\" \"$@\" < /tmp/stdin-test", "cat"], null, "/tmp");
            Assert.Equal(0, cat.ExitCode);
            Assert.Equal("hello sandbox", cat.StdOut);

            var env = await docker.ExecAsync(name, ["/bin/sh", "-c", "printf %s \"$NAUDIT_T\""],
                new Dictionary<string, string?> { ["NAUDIT_T"] = "42" }, "/tmp");
            Assert.Equal("42", env.StdOut);

            var fail = await docker.ExecAsync(name, ["/bin/sh", "-c", "echo boom >&2; exit 3"], null, "/tmp");
            Assert.Equal(3, fail.ExitCode);
            Assert.Contains("boom", fail.StdErr);

            var listed = await docker.ListContainersAsync("naudit-test-");
            Assert.Contains(listed, e => e.Name == name && e.Running);

            await docker.StopAsync(name);
            Assert.False((await docker.InspectContainerAsync(name))!.Running);
            await docker.StartAsync(name);
            Assert.True((await docker.InspectContainerAsync(name))!.Running);
        }
        finally
        {
            await docker.RemoveContainerAsync(name);
            await docker.RemoveVolumeAsync(name);
        }
        Assert.Null(await docker.InspectContainerAsync(name));
    }

    [Fact]
    public async Task Ping_missingSocket_isFalse_notThrow()
    {
        if (!Enabled) return;
        using var docker = new SocketDockerClient("/nonexistent/docker.sock");
        Assert.False(await docker.PingAsync());
    }
}
