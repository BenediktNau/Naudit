using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Setup;
using Xunit;

namespace Naudit.Tests;

/// <summary>Die dreiwertige Registrierung von ICommentEventProbe (GitHub+App -> GitHubApp-Probe,
/// GitLab -> GitLab-Probe, GitHub+Pat -> gar kein Probe) hat sonst keinen Test, der eine
/// verlorene/falsch verzweigte Registrierung auffangen würde — genau die stille Fehlerklasse,
/// die dieses Feature selbst aufdecken soll. Mirror von GitTokenWiringTests.</summary>
public class CommentEventProbeWiringTests
{
    private static readonly string TestPem = CreateTestPem();
    private static string CreateTestPem()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        // GitLabCommentEventProbe zieht NauditDbContext aus dem Container — anders als bei
        // GitTokenWiringTests wird die Registrierung hier tatsächlich aufgelöst (GetRequiredService),
        // also muss die DB-Registrierung wie in DbWiringTests mit dabei sein.
        settings["Naudit:Db:ConnectionString"] = "Data Source=unused.db";
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNauditDatabase(config);
        services.AddNauditInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void GitHub_authApp_resolvesGitHubAppCommentEventProbe()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitHub",
            ["Naudit:GitHub:Auth"] = "App",
            ["Naudit:GitHub:App:AppId"] = "12345",
            ["Naudit:GitHub:App:PrivateKey"] = TestPem,
        });
        using var scope = sp.CreateScope();

        Assert.IsType<GitHubAppCommentEventProbe>(scope.ServiceProvider.GetRequiredService<ICommentEventProbe>());
    }

    [Fact]
    public void GitLab_resolvesGitLabCommentEventProbe()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitLab",
            ["Naudit:GitLab:Token"] = "tok",
        });
        using var scope = sp.CreateScope();

        Assert.IsType<GitLabCommentEventProbe>(scope.ServiceProvider.GetRequiredService<ICommentEventProbe>());
    }

    [Fact]
    public void GitHub_authPat_resolvesNoProbeAtAll()
    {
        // Kern der Lücke, die dieser Test schließt: im PAT-Modus gibt es keine App, deren
        // Ereignisliste man abfragen könnte — CommentEventCheckService muss dann untätig bleiben.
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitHub",
            ["Naudit:GitHub:Token"] = "tok",
        });
        using var scope = sp.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<ICommentEventProbe>());
    }
}
