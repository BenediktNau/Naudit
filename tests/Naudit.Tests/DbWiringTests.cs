using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Ui;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>DB/UI sind immer an; die Zugangsschranke hängt nur noch an Naudit:AccessGate:Mode
/// (Open = AllowAll = Pre-WebUI-Verhalten, Registered = EfAccessGate).</summary>
public class DbWiringTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public DbWiringTests(TestAppFactory factory) => _factory = factory;

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNauditDatabase(config);
        services.AddNauditInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void DefaultMode_Open_registriertAllowAllGate_undEfSink()
    {
        using var sp = Build(new() { ["Naudit:Db:ConnectionString"] = "Data Source=unused.db" });
        using var scope = sp.CreateScope();
        Assert.IsType<AllowAllAccessGate>(scope.ServiceProvider.GetRequiredService<IAccessGate>());
        Assert.IsType<EfReviewAuditSink>(scope.ServiceProvider.GetRequiredService<IReviewAuditSink>());
    }

    [Fact]
    public void ModeRegistered_registriertEfGate()
    {
        using var sp = Build(new()
        {
            ["Naudit:Db:ConnectionString"] = "Data Source=unused.db",
            ["Naudit:AccessGate:Mode"] = "Registered",
        });
        using var scope = sp.CreateScope();
        Assert.IsType<EfAccessGate>(scope.ServiceProvider.GetRequiredService<IAccessGate>());
    }

    [Fact]
    public async Task UiEndpoints_sindImmerGemappt()
    {
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitLab");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "s");
        }).CreateClient();

        // /api/accounts existiert jetzt immer — 401 (nicht eingeloggt, RequireAuthorization)
        // statt 404 (Route früher nur bei aktivierter WebUI gemappt). /api/me selbst ist bewusst
        // anonym erreichbar (Status-Endpoint fürs SPA-AuthGate), daher hier kein guter Testkandidat.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
}
