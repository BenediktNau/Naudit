using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Redaction;
using Xunit;

namespace Naudit.Tests;

public class RedactionWiringTests
{
    // Resolved bewusst NUR den Redactor (kein IGitPlatform) — entkoppelt vom GitLab-BaseUrl-Wiring.
    private static IPromptRedactor Resolve(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNauditInfrastructure(config);
        using var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IPromptRedactor>();
    }

    [Fact]
    public void MissingSection_defaultsToPatternRedactor()
    {
        // Default AN: keine Redaction-Section ⇒ aktiver PatternRedactor.
        Assert.IsType<PatternRedactor>(Resolve(new() { ["Naudit:Git:Platform"] = "GitLab" }));
    }

    [Fact]
    public void Enabled_registersPatternRedactor()
    {
        Assert.IsType<PatternRedactor>(Resolve(new()
        {
            ["Naudit:Git:Platform"] = "GitLab",
            ["Naudit:Redaction:Enabled"] = "true",
        }));
    }

    [Fact]
    public void Disabled_registersNullRedactor()
    {
        Assert.IsType<NullPromptRedactor>(Resolve(new()
        {
            ["Naudit:Git:Platform"] = "GitLab",
            ["Naudit:Redaction:Enabled"] = "false",
        }));
    }
}
