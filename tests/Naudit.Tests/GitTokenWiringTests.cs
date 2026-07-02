using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
using Xunit;

namespace Naudit.Tests;

public class GitTokenWiringTests
{
    // Einmalig generiertes Test-PEM (kein Fixture-Secret im Repo).
    private static readonly string TestPem = CreateTestPem();
    private static string CreateTestPem()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNauditInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task GitHub_resolvesProviderFromGitHubSection()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitHub",
            ["Naudit:GitHub:Token"] = "default-tok",
            ["Naudit:GitHub:ProjectTokens:0:Project"] = "octo/repo",
            ["Naudit:GitHub:ProjectTokens:0:Token"] = "proj-tok",
        });

        var provider = sp.GetRequiredService<IGitTokenProvider>();
        Assert.Equal("proj-tok", await provider.ResolveTokenAsync("octo/repo"));
        Assert.Equal("default-tok", await provider.ResolveTokenAsync("octo/other"));
    }

    [Fact]
    public async Task GitLab_resolvesProviderFromGitLabSection()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitLab",
            ["Naudit:GitLab:Token"] = "default-tok",
            ["Naudit:GitLab:ProjectTokens:0:Project"] = "12345",
            ["Naudit:GitLab:ProjectTokens:0:Token"] = "proj-tok",
        });

        var provider = sp.GetRequiredService<IGitTokenProvider>();
        Assert.Equal("proj-tok", await provider.ResolveTokenAsync("12345"));
        Assert.Equal("default-tok", await provider.ResolveTokenAsync("999"));
    }

    [Fact]
    public async Task NoProjectTokens_configured_alwaysResolvesDefault()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitHub",
            ["Naudit:GitHub:Token"] = "default-tok",
        });

        var provider = sp.GetRequiredService<IGitTokenProvider>();
        Assert.Equal("default-tok", await provider.ResolveTokenAsync("octo/anything"));
    }

    [Fact]
    public void GitHub_authApp_resolvesGitHubAppTokenProvider()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitHub",
            ["Naudit:GitHub:Auth"] = "App",
            ["Naudit:GitHub:App:AppId"] = "12345",
            ["Naudit:GitHub:App:PrivateKey"] = TestPem,
        });
        Assert.IsType<GitHubAppTokenProvider>(sp.GetRequiredService<IGitTokenProvider>());
    }

    [Fact]
    public void GitHub_defaultAuth_resolvesConfiguredGitTokenProvider()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitHub",
            ["Naudit:GitHub:Token"] = "tok",
        });
        Assert.IsType<ConfiguredGitTokenProvider>(sp.GetRequiredService<IGitTokenProvider>());
    }

    [Fact]
    public void GitHub_authApp_withoutKey_failsFastAtStartup()
    {
        Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["Naudit:Git:Platform"] = "GitHub",
            ["Naudit:GitHub:Auth"] = "App",
            ["Naudit:GitHub:App:AppId"] = "12345",
            // PrivateKey fehlt absichtlich
        }));
    }
}
