using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Dast;
using Xunit;

namespace Naudit.Tests;

public class DastWiringTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddNauditDatabase(config);
        services.AddNauditInfrastructure(config);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Naudit:Git:Platform"] = "GitLab",
        ["Naudit:GitLab:BaseUrl"] = "https://gitlab.example.com",
    };

    [Fact]
    public void Dast_disabledByDefault_registersNoAppRunner()
    {
        using var provider = Build(BaseSettings());

        Assert.Null(provider.GetService<IAppRunner>());
    }

    [Fact]
    public void Dast_enabled_registersAppRunner_andOrphanSweeper()
    {
        var settings = BaseSettings();
        settings["Naudit:Review:Dast:Enabled"] = "true";
        using var provider = Build(settings);

        Assert.NotNull(provider.GetService<IAppRunner>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is DastOrphanSweeper);
    }
}
