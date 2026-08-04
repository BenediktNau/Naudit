using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Benchmark;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;

namespace Naudit.Tests;

public class BenchmarkWiringTests
{
    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Naudit:Git:Platform"] = "GitHub",
            ["Naudit:GitHub:Token"] = "test-token",
            ["Naudit:GitHub:WebhookSecret"] = "test-secret",
            ["Naudit:Ai:Provider"] = "ClaudeCode",
            ["Naudit:Ai:Model"] = "opus",
            ["Naudit:Db:ConnectionString"] = "Data Source=:memory:",
        })
        .Build();

    [Fact]
    public void AddBenchmarkCapture_ersetzt_IGitPlatform_durch_den_Dekorator()
    {
        var services = new ServiceCollection();
        var config = Config();
        services.AddSingleton(config);
        services.AddNauditDatabase(config);
        services.AddNauditInfrastructure(config);
        services.AddBenchmarkCapture();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var platform = scope.ServiceProvider.GetRequiredService<IGitPlatform>();

        Assert.IsType<CapturingGitPlatform>(platform);
    }

    [Fact]
    public void AddBenchmarkCapture_registriert_ReviewCapture_als_Singleton()
    {
        var services = new ServiceCollection();
        var config = Config();
        services.AddSingleton(config);
        services.AddNauditDatabase(config);
        services.AddNauditInfrastructure(config);
        services.AddBenchmarkCapture();

        using var provider = services.BuildServiceProvider();

        var a = provider.GetRequiredService<ReviewCapture>();
        var b = provider.GetRequiredService<ReviewCapture>();

        Assert.Same(a, b);
    }
}
