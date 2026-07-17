using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Memory;
using Xunit;

namespace Naudit.Tests;

public class ReviewMemoryWiringTests
{
    private static void AssertMemoryType(Dictionary<string, string?> settings, Type expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddNauditDatabase(config);
        services.AddNauditInfrastructure(config);
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        Assert.IsType(expected, scope.ServiceProvider.GetRequiredService<IReviewMemory>());
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Naudit:Git:Platform"] = "GitLab",
        ["Naudit:GitLab:BaseUrl"] = "https://gitlab.example.com",
    };

    [Fact]
    public void Default_registersDbReviewMemory()
        => AssertMemoryType(BaseSettings(), typeof(DbReviewMemory));

    [Fact]
    public void Disabled_registersNullReviewMemory()
    {
        var settings = BaseSettings();
        settings["Naudit:Review:Memory:Enabled"] = "false";
        AssertMemoryType(settings, typeof(NullReviewMemory));
    }
}
