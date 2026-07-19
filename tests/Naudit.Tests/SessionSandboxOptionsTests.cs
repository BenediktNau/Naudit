using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

public class SessionSandboxOptionsTests
{
    [Fact]
    public void Binds_mode_and_subOptions()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Naudit:Ai:SessionSandbox"] = "Docker",
            ["Naudit:Ai:Sandbox:IdleTimeout"] = "1.12:00:00",
            ["Naudit:Ai:Sandbox:MaxLiveContainers"] = "3",
            ["Naudit:Ai:Sandbox:DockerSocketPath"] = "/run/user/1000/docker.sock",
            ["Naudit:Ai:Sandbox:Image"] = "ghcr.io/benediktnau/naudit:v1.2.3",
        }).Build();

        var ai = config.GetSection("Naudit:Ai").Get<AiOptions>()!;
        var sandbox = config.GetSection("Naudit:Ai:Sandbox").Get<SessionSandboxOptions>()!;

        Assert.Equal(SessionSandbox.Docker, ai.SessionSandbox);
        Assert.Equal(TimeSpan.FromHours(36), sandbox.IdleTimeout);
        Assert.Equal(3, sandbox.MaxLiveContainers);
        Assert.Equal("/run/user/1000/docker.sock", sandbox.DockerSocketPath);
        Assert.Equal("ghcr.io/benediktnau/naudit:v1.2.3", sandbox.Image);
    }

    [Fact]
    public void Defaults_areOff_withTwoDayIdle()
    {
        var config = new ConfigurationBuilder().Build();

        var ai = config.GetSection("Naudit:Ai").Get<AiOptions>() ?? new AiOptions();
        var sandbox = config.GetSection("Naudit:Ai:Sandbox").Get<SessionSandboxOptions>() ?? new SessionSandboxOptions();

        Assert.Equal(SessionSandbox.None, ai.SessionSandbox);
        Assert.Equal(TimeSpan.FromDays(2), sandbox.IdleTimeout);
        Assert.Equal(5, sandbox.MaxLiveContainers);
        Assert.Equal("/var/run/docker.sock", sandbox.DockerSocketPath);
        Assert.Null(sandbox.Image);
    }

    [Fact]
    public void Catalog_hasSandboxKeys()
    {
        Assert.True(SettingsCatalog.TryGet("Naudit:Ai:SessionSandbox", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Ai:Sandbox:IdleTimeout", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Ai:Sandbox:MaxLiveContainers", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Ai:Sandbox:DockerSocketPath", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Ai:Sandbox:Image", out _));
    }
}
