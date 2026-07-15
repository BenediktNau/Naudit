using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Xunit;

namespace Naudit.Tests;

public class AiClientRouterWiringTests
{
    private static void AssertRouterType(Dictionary<string, string?> settings, Type expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddNauditDatabase(config);       // ClaudeSessionService (Scoped) für Author-/RoundRobin-Router
        services.AddNauditInfrastructure(config);
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        Assert.IsType(expected, scope.ServiceProvider.GetRequiredService<IAiClientRouter>());
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Naudit:Git:Platform"] = "GitLab",
        ["Naudit:GitLab:BaseUrl"] = "https://gitlab.example.com",
    };

    [Fact]
    public void Default_registersSingleClientRouter()
        => AssertRouterType(BaseSettings(), typeof(SingleClientRouter));

    [Theory]
    [InlineData("Single", typeof(SingleClientRouter))]
    [InlineData("Author", typeof(AuthorSessionRouter))]
    [InlineData("RoundRobin", typeof(RoundRobinSessionRouter))]
    public void SessionRouting_selectsMatchingRouter(string mode, Type expected)
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:SessionRouting"] = mode;
        AssertRouterType(settings, expected);
    }
}
