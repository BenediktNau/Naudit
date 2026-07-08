using System.Net;
using Microsoft.AspNetCore.Hosting;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>Kaputte Review-Config (GitHub App ohne Keys) ⇒ kein Crash, sondern Recovery:
/// Health + UI laufen, Webhooks/Review sind nicht gemappt.</summary>
public class RecoveryModeTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public RecoveryModeTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task KaputteConfig_startetRecoveryStattCrash()
    {
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitHub");
            b.UseSetting("Naudit:GitHub:Auth", "App"); // AppId/PrivateKey fehlen ⇒ Registrierung wirft
        }).CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        // Webhooks sind im Recovery-Modus NICHT gemappt: es existiert kein POST-Handler auf dem Pfad.
        // Der einzige treffende Endpoint ist das GET-only SPA-Catch-All (MapFallbackToFile) ⇒ die
        // Methoden-Policy antwortet 405 (nicht 404). Im Gesundfall lieferte der echte Handler 401/200 —
        // 405 beweist also die Abwesenheit des Webhook-Handlers.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/webhook/github", null)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/webhook/gitlab", null)).StatusCode);
        // /api/me ist bewusst anonym (Status-Endpoint fürs SPA) — daher /api/accounts prüfen:
        // RequireAuthorization ⇒ 401 = die UI ist gemappt und lebt (kein 404 = nicht gemappt).
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/accounts")).StatusCode); // UI lebt
    }
}
