using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Dast;
using Naudit.Infrastructure.Docker;
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

    /// <summary>Sind Session-Sandbox UND DAST gleichzeitig aktiv, teilen sie sich einen
    /// IDockerClient — der Sandbox-Socket-Pfad muss gewinnen (andere Risikoklasse, siehe
    /// docs/dast.md#docker-socket-sharing). Ein invertierter Vorrang würde diesen Test kippen.</summary>
    [Fact]
    public void Dast_andSessionSandbox_bothEnabled_sandboxSocketPathWins()
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:SessionSandbox"] = "Docker";
        settings["Naudit:Ai:Sandbox:DockerSocketPath"] = "/tmp/sandbox-test.sock";
        settings["Naudit:Review:Dast:Enabled"] = "true";
        settings["Naudit:Review:Dast:DockerSocketPath"] = "/tmp/dast-test.sock";
        using var provider = Build(settings);

        var client = provider.GetRequiredService<IDockerClient>();

        Assert.Equal("/tmp/sandbox-test.sock", Assert.IsType<SocketDockerClient>(client).SocketPath);
    }
}
