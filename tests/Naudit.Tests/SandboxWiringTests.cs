using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Ai.Sandbox;
using Xunit;

namespace Naudit.Tests;

public class SandboxWiringTests
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
    public void Default_usesInProcessRunnerFactory()
    {
        using var sp = Build(BaseSettings());
        Assert.IsType<InProcessSessionRunnerFactory>(sp.GetRequiredService<ISessionRunnerFactory>());
    }

    [Fact]
    public void DockerMode_usesDockerRunnerFactory_andRegistersManager()
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:SessionSandbox"] = "Docker";
        using var sp = Build(settings);

        Assert.IsType<DockerSessionRunnerFactory>(sp.GetRequiredService<ISessionRunnerFactory>());
        Assert.NotNull(sp.GetRequiredService<SessionContainerManager>());
        Assert.NotNull(sp.GetRequiredService<SessionSandboxState>());
    }

    [Fact]
    public void DockerMode_registersSweeperHostedService()
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:SessionSandbox"] = "Docker";
        using var sp = Build(settings);
        Assert.Contains(sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            s => s is SandboxSweeperService);
    }

    [Fact]
    public void Default_registersNoSweeper()
    {
        using var sp = Build(BaseSettings());
        Assert.DoesNotContain(sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            s => s is SandboxSweeperService);
    }
}
