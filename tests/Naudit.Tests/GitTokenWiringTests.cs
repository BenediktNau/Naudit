using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Git;
using Xunit;

namespace Naudit.Tests;

public class GitTokenWiringTests
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
    public void GitHub_resolvesProviderFromGitHubSection()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitHub",
            ["Naudit:GitHub:Token"] = "default-tok",
            ["Naudit:GitHub:ProjectTokens:0:Project"] = "octo/repo",
            ["Naudit:GitHub:ProjectTokens:0:Token"] = "proj-tok",
        });

        var provider = sp.GetRequiredService<IGitTokenProvider>();
        Assert.Equal("proj-tok", provider.ResolveToken("octo/repo"));
        Assert.Equal("default-tok", provider.ResolveToken("octo/other"));
    }

    [Fact]
    public void GitLab_resolvesProviderFromGitLabSection()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitLab",
            ["Naudit:GitLab:Token"] = "default-tok",
            ["Naudit:GitLab:ProjectTokens:0:Project"] = "12345",
            ["Naudit:GitLab:ProjectTokens:0:Token"] = "proj-tok",
        });

        var provider = sp.GetRequiredService<IGitTokenProvider>();
        Assert.Equal("proj-tok", provider.ResolveToken("12345"));
        Assert.Equal("default-tok", provider.ResolveToken("999"));
    }

    [Fact]
    public void NoProjectTokens_configured_alwaysResolvesDefault()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitHub",
            ["Naudit:GitHub:Token"] = "default-tok",
        });

        var provider = sp.GetRequiredService<IGitTokenProvider>();
        Assert.Equal("default-tok", provider.ResolveToken("octo/anything"));
    }
}
