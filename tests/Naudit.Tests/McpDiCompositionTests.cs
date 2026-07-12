using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Mcp;
using Xunit;

namespace Naudit.Tests;

public class McpDiCompositionTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNauditInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void McpDisabled_resolvesNullToolProvider()
    {
        using var sp = Build(new Dictionary<string, string?>
        {
            ["Naudit:Ai:Provider"] = "OpenAICompatible",
            ["Naudit:Ai:ApiKey"] = "k",
            ["Naudit:Review:Mcp:Enabled"] = "false",
        });

        Assert.IsType<NullReviewToolProvider>(sp.GetRequiredService<IReviewToolProvider>());
    }

    [Fact]
    public void McpEnabled_meaiProvider_resolvesMcpToolProvider()
    {
        using var sp = Build(new Dictionary<string, string?>
        {
            ["Naudit:Ai:Provider"] = "OpenAICompatible",
            ["Naudit:Ai:ApiKey"] = "k",
            ["Naudit:Review:Mcp:Enabled"] = "true",
            ["Naudit:Review:Mcp:Servers:0:Name"] = "context7",
            ["Naudit:Review:Mcp:Servers:0:Url"] = "https://mcp.context7.com/mcp",
        });

        Assert.IsType<McpReviewToolProvider>(sp.GetRequiredService<IReviewToolProvider>());
    }

    [Fact]
    public void McpEnabled_claudeCodeProvider_stillResolvesNullToolProvider()
    {
        using var sp = Build(new Dictionary<string, string?>
        {
            ["Naudit:Ai:Provider"] = "ClaudeCode",
            ["Naudit:Review:Mcp:Enabled"] = "true",
            ["Naudit:Review:Mcp:Servers:0:Name"] = "context7",
            ["Naudit:Review:Mcp:Servers:0:Url"] = "https://mcp.context7.com/mcp",
        });

        // ClaudeCode nutzt CLI-natives MCP ⇒ kein ChatOptions.Tools-Provider.
        Assert.IsType<NullReviewToolProvider>(sp.GetRequiredService<IReviewToolProvider>());
    }
}
