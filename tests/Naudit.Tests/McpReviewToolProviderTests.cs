using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Mcp;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class McpReviewToolProviderTests
{
    private static readonly ReviewRequest Request = new("1", 1, "T");

    private static McpServerConfig Server(string name) => new() { Name = name, Transport = "http", Url = "http://x" };
    private static AITool Tool(string name) => AIFunctionFactory.Create(() => "r", name);

    private static McpReviewToolProvider Provider(McpOptions opts, IMcpToolConnector connector)
        => new(opts, connector, NullLogger<McpReviewToolProvider>.Instance);

    [Fact]
    public async Task Disabled_returnsEmpty_andNeverCallsConnector()
    {
        var connector = new FakeMcpToolConnector().Returns("a", Tool("t"));
        var opts = new McpOptions { Enabled = false, Servers = { Server("a") } };

        var tools = await Provider(opts, connector).GetToolsAsync(Request);

        Assert.Empty(tools);
        Assert.Equal(0, connector.CallCount);
    }

    [Fact]
    public async Task Aggregates_toolsFromAllServers()
    {
        var connector = new FakeMcpToolConnector().Returns("a", Tool("t1")).Returns("b", Tool("t2"));
        var opts = new McpOptions { Enabled = true, Servers = { Server("a"), Server("b") } };

        var tools = await Provider(opts, connector).GetToolsAsync(Request);

        Assert.Equal(2, tools.Count);
    }

    [Fact]
    public async Task FailingServer_isSkipped_othersStillReturn()
    {
        var connector = new FakeMcpToolConnector().Throws("a").Returns("b", Tool("t2"));
        var opts = new McpOptions { Enabled = true, Servers = { Server("a"), Server("b") } };

        var tools = await Provider(opts, connector).GetToolsAsync(Request);

        var tool = Assert.Single(tools);
        Assert.Equal("t2", tool.Name);
    }

    [Fact]
    public async Task Caches_nonEmptyResult_acrossCalls()
    {
        var connector = new FakeMcpToolConnector().Returns("a", Tool("t1"));
        var opts = new McpOptions { Enabled = true, Servers = { Server("a") } };
        var provider = Provider(opts, connector);

        await provider.GetToolsAsync(Request);
        await provider.GetToolsAsync(Request);

        Assert.Equal(1, connector.CallCount);   // zweiter Review nutzt den Cache
    }
}
