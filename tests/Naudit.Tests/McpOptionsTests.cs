using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Mcp;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

public class McpOptionsTests
{
    [Fact]
    public void Binds_enabled_iterations_and_serverList()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Naudit:Review:Mcp:Enabled"] = "true",
            ["Naudit:Review:Mcp:MaxIterations"] = "6",
            ["Naudit:Review:Mcp:Servers:0:Name"] = "context7",
            ["Naudit:Review:Mcp:Servers:0:Transport"] = "http",
            ["Naudit:Review:Mcp:Servers:0:Url"] = "https://mcp.context7.com/mcp",
            ["Naudit:Review:Mcp:Servers:0:ApiKey"] = "sk-123",
        }).Build();

        var opts = config.GetSection("Naudit:Review:Mcp").Get<McpOptions>()!;

        Assert.True(opts.Enabled);
        Assert.Equal(6, opts.MaxIterations);
        var server = Assert.Single(opts.Servers);
        Assert.Equal("context7", server.Name);
        Assert.Equal("https://mcp.context7.com/mcp", server.Url);
        Assert.Equal("sk-123", server.ApiKey);
    }

    [Fact]
    public void Catalog_hasEnabledAndMaxIterationsScalars()
    {
        Assert.True(SettingsCatalog.TryGet("Naudit:Review:Mcp:Enabled", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Review:Mcp:MaxIterations", out _));
    }
}
