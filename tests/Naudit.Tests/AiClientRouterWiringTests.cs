using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Xunit;

namespace Naudit.Tests;

public class AiClientRouterWiringTests
{
    private static void AssertRouterType<T>(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddNauditDatabase(config);       // ClaudeSessionService (Scoped) für den AuthorSessionRouter
        services.AddNauditInfrastructure(config);
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        Assert.IsType<T>(scope.ServiceProvider.GetRequiredService<IAiClientRouter>());
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Naudit:Git:Platform"] = "GitLab",
        ["Naudit:GitLab:BaseUrl"] = "https://gitlab.example.com",
    };

    [Fact]
    public void Default_registersSingleClientRouter()
        => AssertRouterType<SingleClientRouter>(BaseSettings());

    [Fact]
    public void Enabled_registersAuthorSessionRouter()
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:AuthorSessions:Enabled"] = "true";
        AssertRouterType<AuthorSessionRouter>(settings);
    }
}
