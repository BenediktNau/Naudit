using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>Wizard-API. BEWUSST kein IClassFixture: jeder Test bekommt seine eigene
/// TestAppFactory (frische DB) — die Asserts haengen von "existiert schon ein Admin?" ab
/// und duerfen sich ueber eine geteilte Fixture-DB nicht gegenseitig beeinflussen.</summary>
public class SetupEndpointTests
{
    /// <summary>Setup-Modus erzwingen: GitLab-Pflichtwerte der Baseline leeren.</summary>
    private static WebApplicationFactory<Program> SetupMode(TestAppFactory factory,
        Action<IWebHostBuilder>? extra = null) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:GitLab:Token", "");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "");
            extra?.Invoke(b);
        });

    /// <summary>Legt den ersten Admin ueber die Wizard-API an (frische DB vorausgesetzt) —
    /// ab Task 5-7 der Standard-Einstieg fuer eingeloggte Wizard-Tests.</summary>
    private static async Task<HttpClient> LoggedInAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/setup/admin",
            new { username = "wizard-admin", password = "pw-123456" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return client;
    }

    [Fact]
    public async Task Status_imSetupModus_meldetRequiredUndMissing()
    {
        using var app = new TestAppFactory();
        var client = SetupMode(app).CreateClient();
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/status"));
        Assert.True(doc.RootElement.GetProperty("setupRequired").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("adminExists").GetBoolean());
        Assert.Contains(doc.RootElement.GetProperty("missing").EnumerateArray(),
            m => m.GetString() == "Naudit:GitLab:Token");
        Assert.Equal("http://localhost", doc.RootElement.GetProperty("suggestedPublicBaseUrl").GetString());
    }

    [Fact]
    public async Task Status_konfiguriert_meldetNichtRequired_undAdminApiFehlt()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient(); // Baseline = konfiguriert
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/status"));
        Assert.False(doc.RootElement.GetProperty("setupRequired").GetBoolean());
        // Wizard-API ist nicht gemappt ⇒ der /api-Fallback antwortet 404.
        var res = await client.PostAsJsonAsync("/api/setup/admin", new { username = "a", password = "pw-123456" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task AdminAnlegen_loggtEin_zweiterVersuchIst409()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));

        // Cookie-Session aktiv: ein RequireAuthorization-Endpoint antwortet nicht mehr 401.
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/setup/draft")).StatusCode);

        var second = await client.PostAsJsonAsync("/api/setup/admin", new { username = "x", password = "pw-123456" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task AdminAnlegen_beiGeseedetemAdmin_ist409()
    {
        using var app = new TestAppFactory();
        var client = SetupMode(app, b =>
        {
            b.UseSetting("Naudit:Ui:Admin:Username", "root");
            b.UseSetting("Naudit:Ui:Admin:InitialPassword", "passwort123");
        }).CreateClient();
        var res = await client.PostAsJsonAsync("/api/setup/admin", new { username = "x", password = "pw-123456" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task AdminAnlegen_kurzesPasswort_ist400()
    {
        using var app = new TestAppFactory();
        var client = SetupMode(app).CreateClient();
        var res = await client.PostAsJsonAsync("/api/setup/admin", new { username = "kurz", password = "1234567" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task DraftApi_ohneLogin_ist401()
    {
        using var app = new TestAppFactory();
        var client = SetupMode(app).CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/setup/draft")).StatusCode);
    }
}
