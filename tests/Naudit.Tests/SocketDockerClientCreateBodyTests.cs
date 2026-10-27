using System.Text.Json;
using Naudit.Infrastructure.Docker;
using Xunit;

namespace Naudit.Tests;

/// <summary>Unit-Tests des Create-Bodys (POST /containers/create) — ohne Docker. Die Härtung der
/// Session-Container steckt komplett im HostConfig dieses Bodys.</summary>
public class SocketDockerClientCreateBodyTests
{
    private static JsonElement HostConfig(ContainerRunSpec spec)
    {
        using var doc = JsonDocument.Parse(SocketDockerClient.BuildCreateBody(spec));
        return doc.RootElement.GetProperty("HostConfig").Clone();
    }

    private static readonly ContainerRunSpec Spec =
        new("naudit-session-7", "sha256:img", "naudit-session-7", "/home/app", []) { Entrypoint = ["sleep", "infinity"] };

    [Fact]
    public void Create_dropsAllCapabilities_andForbidsPrivilegeEscalation()
    {
        var host = HostConfig(Spec);

        Assert.Equal(["ALL"], host.GetProperty("CapDrop").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["no-new-privileges"], host.GetProperty("SecurityOpt").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Create_setsMemoryAndPidsLimit_fromSpec()
    {
        var host = HostConfig(Spec with { MemoryBytes = 2L * 1024 * 1024 * 1024, PidsLimit = 512 });

        Assert.Equal(2L * 1024 * 1024 * 1024, host.GetProperty("Memory").GetInt64());
        Assert.Equal(512, host.GetProperty("PidsLimit").GetInt64());
    }

    [Fact]
    public void Create_omitsLimits_whenUnsetOrZero()
    {
        // 0/null = unbegrenzt ⇒ Feld weglassen (Engine-Default), statt 0 zu schicken.
        var host = HostConfig(Spec with { MemoryBytes = 0, PidsLimit = null });

        Assert.False(host.TryGetProperty("Memory", out _));
        Assert.False(host.TryGetProperty("PidsLimit", out _));
    }

    [Fact]
    public void Create_keepsBindsEntrypointAndPascalCaseFields()
    {
        using var doc = JsonDocument.Parse(SocketDockerClient.BuildCreateBody(Spec));
        var root = doc.RootElement;

        Assert.Equal("sha256:img", root.GetProperty("Image").GetString());
        Assert.Equal(["sleep", "infinity"], root.GetProperty("Entrypoint").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["naudit-session-7:/home/app"],
            root.GetProperty("HostConfig").GetProperty("Binds").EnumerateArray().Select(e => e.GetString()));
    }
}
