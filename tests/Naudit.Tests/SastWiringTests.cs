using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Xunit;

namespace Naudit.Tests;

public class SastWiringTests
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
    public void Enabled_registersSelectedAnalyzers()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitLab",
            ["Naudit:Sast:Enabled"] = "true",
            ["Naudit:Sast:Analyzers:0"] = "opengrep",
            ["Naudit:Sast:Analyzers:1"] = "trivy",
        });

        using var scope = sp.CreateScope();
        var analyzers = scope.ServiceProvider.GetServices<ISastAnalyzer>().ToList();

        Assert.Equal(2, analyzers.Count);
        Assert.Contains(analyzers, a => a.Name == "opengrep");
        Assert.Contains(analyzers, a => a.Name == "trivy");
    }

    [Fact]
    public void Disabled_registersNoAnalyzers_butReviewServiceResolves()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitLab",
            ["Naudit:Sast:Enabled"] = "false",
        });

        using var scope = sp.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetServices<ISastAnalyzer>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Naudit.Core.Review.ReviewService>());
    }

    [Fact]
    public void Enabled_withNoAnalyzersConfig_registersDefaultOpengrepAndTrivy()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitLab",
            ["Naudit:Sast:Enabled"] = "true",
        });

        using var scope = sp.CreateScope();
        var analyzers = scope.ServiceProvider.GetServices<ISastAnalyzer>().ToList();

        Assert.Equal(2, analyzers.Count);
        Assert.Contains(analyzers, a => a.Name == "opengrep");
        Assert.Contains(analyzers, a => a.Name == "trivy");
    }

    [Fact]
    public void Enabled_registersGitleaks()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitLab",
            ["Naudit:Sast:Enabled"] = "true",
            ["Naudit:Sast:Analyzers:0"] = "gitleaks",
        });

        using var scope = sp.CreateScope();
        var analyzers = scope.ServiceProvider.GetServices<ISastAnalyzer>().ToList();

        Assert.Contains(analyzers, a => a.Name == "gitleaks");
    }

    [Fact]
    public void Enabled_registersOsvScanner()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitLab",
            ["Naudit:Sast:Enabled"] = "true",
            ["Naudit:Sast:Analyzers:0"] = "osv-scanner",
        });

        using var scope = sp.CreateScope();
        var analyzers = scope.ServiceProvider.GetServices<ISastAnalyzer>().ToList();

        Assert.Contains(analyzers, a => a.Name == "osv-scanner");
    }

    [Fact]
    public void Enabled_withUnknownAnalyzer_throws()
    {
        Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["Naudit:Git:Platform"] = "GitLab",
            ["Naudit:Sast:Enabled"] = "true",
            ["Naudit:Sast:Analyzers:0"] = "bandit",
        }));
    }
}
