using System.Net;
using Microsoft.AspNetCore.Hosting;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>Fehlende Pflicht-Config ⇒ Setup-Modus: Health + UI-Basis laufen,
/// Webhooks/Review sind nicht gemappt (405 = nur das GET-only SPA-Fallback trifft).</summary>
public class SetupModeTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public SetupModeTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task FehlendePflichtConfig_startetSetupModus_ohneWebhooks()
    {
        // Baseline der TestAppFactory gezielt leeren: "" zaehlt fuer SetupStatus als fehlend.
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:GitLab:Token", "");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "");
        }).CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/webhook/gitlab", null)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/webhook/github", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/accounts")).StatusCode); // UI-Basis lebt
    }

    [Fact]
    public async Task KompletteConfig_laesstWebhookGemappt()
    {
        // Baseline unveraendert = konfiguriert: der GitLab-Webhook existiert (401 wegen fehlendem Token-Header).
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/webhook/gitlab", null)).StatusCode);
    }
}
