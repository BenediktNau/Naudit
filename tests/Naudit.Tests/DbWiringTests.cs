using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Ui;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>Verdrahtung der zwei Schalter: Naudit:Db:Enabled (DbContext + Gate + Audit-Sink)
/// und Naudit:Ui:Enabled (Dashboard/Auth/Accounts) — DB ohne UI ist gültig, UI ohne DB nicht.</summary>
public class DbWiringTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public DbWiringTests(TestAppFactory factory) => _factory = factory;

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNauditInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void DbOn_registersEfGateAndSink()
    {
        using var sp = Build(new()
        {
            ["Naudit:Db:Enabled"] = "true",
            ["Naudit:Db:ConnectionString"] = "Data Source=unused.db",
        });
        using var scope = sp.CreateScope();
        Assert.IsType<EfAccessGate>(scope.ServiceProvider.GetRequiredService<IAccessGate>());
        Assert.IsType<EfReviewAuditSink>(scope.ServiceProvider.GetRequiredService<IReviewAuditSink>());
    }

    [Fact]
    public void DbOff_registersNoOps()
    {
        using var sp = Build(new());
        using var scope = sp.CreateScope();
        Assert.IsType<AllowAllAccessGate>(scope.ServiceProvider.GetRequiredService<IAccessGate>());
        Assert.IsType<NullReviewAuditSink>(scope.ServiceProvider.GetRequiredService<IReviewAuditSink>());
    }

    [Fact]
    public async Task DbOnUiOff_uiEndpointsNotMapped()
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-dbwiring-{Guid.NewGuid():N}.db")}";
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitLab");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "s");
            b.UseSetting("Naudit:Db:Enabled", "true");
            b.UseSetting("Naudit:Db:ConnectionString", db);
        }).CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode); // Host läuft & DB migriert
    }

    [Fact]
    public void UiOn_dbOff_failsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["Naudit:Ui:Enabled"] = "true",
            // Naudit:Db:Enabled fehlt absichtlich
        }));
        Assert.Contains("Naudit:Db:Enabled", ex.Message);
    }
}
