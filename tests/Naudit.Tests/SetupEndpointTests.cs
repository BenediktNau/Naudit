using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Setup;
using Naudit.Tests.Fakes;
using Naudit.Web;
using Naudit.Web.Endpoints;
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

    [Fact]
    public async Task Draft_putUndGet_maskiertSecrets_behaeltSieBeimUpdate()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));
        var put = await client.PutAsJsonAsync("/api/setup/draft", new
        {
            platform = "GitHub", gitToken = "ghp-geheim", webhookSecret = "hook-1", aiProvider = "Ollama",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/draft"));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("draft").GetProperty("gitToken").ValueKind);
        Assert.True(doc.RootElement.GetProperty("hasGitToken").GetBoolean());
        // Generiertes WebhookSecret ist bewusst sichtbar (Copy-Paste in die Plattform).
        Assert.Equal("hook-1", doc.RootElement.GetProperty("draft").GetProperty("webhookSecret").GetString());

        // Update OHNE gitToken (SPA kann den maskierten Wert nicht mitschicken) ⇒ Token bleibt.
        await client.PutAsJsonAsync("/api/setup/draft", new { platform = "GitHub", webhookSecret = "hook-1", aiModel = "m" });
        doc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/draft"));
        Assert.True(doc.RootElement.GetProperty("hasGitToken").GetBoolean());

        // Plattformwechsel ⇒ gespeicherter Token verfaellt (GitHub-PAT taugt nicht fuer GitLab).
        await client.PutAsJsonAsync("/api/setup/draft", new { platform = "GitLab" });
        doc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/draft"));
        Assert.False(doc.RootElement.GetProperty("hasGitToken").GetBoolean());
    }

    [Fact]
    public async Task Draft_delete_verwirft()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));
        await client.PutAsJsonAsync("/api/setup/draft", new { platform = "GitLab", gitToken = "glpat-x" });
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/setup/draft")).StatusCode);
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/draft"));
        Assert.False(doc.RootElement.GetProperty("hasGitToken").GetBoolean());
    }

    [Fact]
    public async Task TestAi_erfolg_liefertOkTrue()
    {
        using var app = new TestAppFactory();
        var factory = SetupMode(app, b => b.ConfigureServices(s =>
            s.AddSingleton(new Naudit.Web.AiTestClientFactory(_ => new FakeChatClient("OK")))));
        var client = await LoggedInAsync(factory);
        var res = await client.PostAsJsonAsync("/api/setup/test-ai",
            new { provider = "Ollama", model = "m" });
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task TestAi_fehler_istErgebnisStattStatuscode()
    {
        using var app = new TestAppFactory();
        var factory = SetupMode(app, b => b.ConfigureServices(s =>
            s.AddSingleton(new Naudit.Web.AiTestClientFactory(_ =>
                throw new InvalidOperationException("connection refused")))));
        var client = await LoggedInAsync(factory);
        var res = await client.PostAsJsonAsync("/api/setup/test-ai", new { provider = "Ollama", model = "m" });
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("connection refused", doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task TestAi_unbekannterProvider_ist400()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));
        var res = await client.PostAsJsonAsync("/api/setup/test-ai", new { provider = "Skynet", model = "m" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    private sealed class FakeRestarter : Naudit.Web.IAppRestarter
    {
        public int RestartCalls;
        public bool RestartPending { get; private set; }
        public void RequestRestart() => RestartCalls++;
        public void MarkRestartPending() => RestartPending = true;
    }

    [Fact]
    public async Task Apply_ohneDraft_ist400()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));
        await client.DeleteAsync("/api/setup/draft");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/setup/apply", null)).StatusCode);
    }

    [Fact]
    public async Task Apply_unvollstaendig_ist400MitMissing()
    {
        // Ohne GitHub-Baseline - sonst wuerde die WAF-Baseline (Naudit:GitHub:Token="test-token")
        // die fehlende Draft-Angabe ueberdecken und die Validierung faelschlich durchwinken.
        using var app = new TestAppFactory().WithoutGitHubBaseline();
        var client = await LoggedInAsync(SetupMode(app));
        await client.PutAsJsonAsync("/api/setup/draft", new
        {
            publicBaseUrl = "https://naudit.example.com", platform = "GitHub", webhookSecret = "hook-1",
            aiProvider = "Ollama", aiModel = "m", // GitToken fehlt!
        });
        var res = await client.PostAsync("/api/setup/apply", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Contains(doc.RootElement.GetProperty("missing").EnumerateArray(),
            m => m.GetString() == "Naudit:GitHub:Token");
    }

    [Fact]
    public async Task Apply_vollstaendig_schreibtSettings_loeschtDraft_stoesstNeustartAn()
    {
        // Ohne GitHub-Baseline - der GitHub-Token muss wirklich aus dem Draft in die DB wandern;
        // mit der WAF-Baseline gesetzt waere der Key "env-gesperrt" und wuerde nie geschrieben.
        using var app = new TestAppFactory().WithoutGitHubBaseline();
        var restarter = new FakeRestarter();
        var factory = SetupMode(app, b => b.ConfigureServices(s =>
            s.AddSingleton<Naudit.Web.IAppRestarter>(restarter)));
        var client = await LoggedInAsync(factory);
        await client.PutAsJsonAsync("/api/setup/draft", new
        {
            publicBaseUrl = "https://naudit.example.com", platform = "GitHub", gitToken = "ghp-geheim",
            webhookSecret = "hook-1", aiProvider = "Anthropic", aiModel = "claude-sonnet-5",
            aiApiKey = "sk-geheim", accessGateMode = "Registered",
        });

        var res = await client.PostAsync("/api/setup/apply", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, restarter.RestartCalls);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        var settings = await db.Settings.ToListAsync();
        Assert.Equal("GitHub", settings.Single(s => s.Key == "Naudit:Git:Platform").Value);
        Assert.Equal("Pat", settings.Single(s => s.Key == "Naudit:GitHub:Auth").Value);
        Assert.Equal("https://naudit.example.com", settings.Single(s => s.Key == "Naudit:PublicBaseUrl").Value);
        Assert.Equal("Registered", settings.Single(s => s.Key == "Naudit:AccessGate:Mode").Value);
        // Secrets liegen verschluesselt, nie im Klartext.
        Assert.DoesNotContain(settings, s => s.Value.Contains("ghp-geheim") || s.Value.Contains("sk-geheim"));
        Assert.True(settings.Single(s => s.Key == "Naudit:GitHub:Token").IsSecret);
        Assert.Empty(await db.SetupDrafts.ToListAsync()); // Draft verbraucht
        // GitLab-Keys des Drafts (leer) wurden NICHT geschrieben.
        Assert.DoesNotContain(settings, s => s.Key.StartsWith("Naudit:GitLab:"));
    }

    [Fact]
    public async Task Apply_envGesetzterKey_wirdNichtGeschrieben_aberZaehltAlsErfuellt()
    {
        // AccessGate:Mode kommt per "env" (UseSetting liegt ueber der DB-Quelle) ⇒ nicht in die DB schreiben.
        using var app = new TestAppFactory();
        var factory = SetupMode(app, b =>
        {
            b.UseSetting("Naudit:AccessGate:Mode", "Open");
            b.ConfigureServices(s => s.AddSingleton<Naudit.Web.IAppRestarter>(new FakeRestarter()));
        });
        var client = await LoggedInAsync(factory);
        await client.PutAsJsonAsync("/api/setup/draft", new
        {
            publicBaseUrl = "https://n.example.com", platform = "GitLab", gitLabBaseUrl = "https://gitlab.example.com",
            gitToken = "glpat-x", webhookSecret = "hook-2", aiProvider = "Ollama", aiModel = "m",
            accessGateMode = "Registered",
        });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/setup/apply", null)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        Assert.DoesNotContain(await db.Settings.ToListAsync(), s => s.Key == "Naudit:AccessGate:Mode");
    }

    [Fact]
    public async Task Draft_appFelder_sindServerseitig_putKannSieNichtSetzen()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));
        // Boeswillig mitgeschickte App-Felder: nur der Manifest-Callback darf sie schreiben.
        await client.PutAsJsonAsync("/api/setup/draft", new
        {
            platform = "GitHub", gitHubAuth = "App",
            gitHubAppId = "1", gitHubAppPrivateKey = "PEM", gitHubAppSlug = "x", gitHubManifestState = "s",
        });
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/draft"));
        Assert.False(doc.RootElement.GetProperty("hasGitHubApp").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("draft").GetProperty("gitHubAppId").ValueKind);
        // Die Wahl-Felder (Auth/Host) sind dagegen normale Draft-Felder:
        Assert.Equal("App", doc.RootElement.GetProperty("draft").GetProperty("gitHubAuth").GetString());
    }

    /// <summary>Draft mit App-Credentials direkt ueber den Service setzen — im echten Fluss
    /// macht das der Manifest-Callback (Task 5). WithoutGitHubBaseline, sonst sind die
    /// GitHub-Keys env-locked und Apply schreibt sie nicht.</summary>
    private static async Task SeedAppDraftAsync(WebApplicationFactory<Program> factory, string gitHubHost)
    {
        using var scope = factory.Services.CreateScope();
        var drafts = scope.ServiceProvider.GetRequiredService<SetupDraftService>();
        await drafts.SaveAsync(System.Text.Json.JsonSerializer.Serialize(new SetupDraft(
            PublicBaseUrl: "https://naudit.example.com",
            Platform: "GitHub",
            WebhookSecret: "hook-geheim",
            AiProvider: "Ollama", AiModel: "m",
            AccessGateMode: "Open",
            GitHubAuth: "App",
            GitHubHost: gitHubHost,
            GitHubAppId: "4711",
            GitHubAppPrivateKey: "PEM-geheim",
            GitHubAppSlug: "naudit-test")));
    }

    [Fact]
    public async Task Apply_appZweig_schreibtAppSettings_ghesBaseUrl_keinePatKeys()
    {
        using var app = new TestAppFactory().WithoutGitHubBaseline();
        var factory = SetupMode(app, b => b.ConfigureServices(s =>
            s.AddSingleton<Naudit.Web.IAppRestarter>(new FakeRestarter())));
        var client = await LoggedInAsync(factory);
        await SeedAppDraftAsync(factory, "https://ghes.example.com");

        var res = await client.PostAsync("/api/setup/apply", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        var settings = db.Settings.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal("App", settings["Naudit:GitHub:Auth"]);
        Assert.True(settings.ContainsKey("Naudit:GitHub:App:AppId"));
        Assert.True(settings.ContainsKey("Naudit:GitHub:App:PrivateKey"));
        Assert.True(settings.ContainsKey("Naudit:GitHub:WebhookSecret"));
        Assert.Equal("https://ghes.example.com/api/v3", settings["Naudit:GitHub:BaseUrl"]); // GHES ⇒ API-Base
        Assert.False(settings.ContainsKey("Naudit:GitHub:Token"));                          // kein PAT-Key
        Assert.DoesNotContain("PEM-geheim", settings["Naudit:GitHub:App:PrivateKey"]);      // verschluesselt
    }

    [Fact]
    public async Task Apply_appZweig_githubCom_schreibtKeineBaseUrl()
    {
        using var app = new TestAppFactory().WithoutGitHubBaseline();
        var factory = SetupMode(app, b => b.ConfigureServices(s =>
            s.AddSingleton<Naudit.Web.IAppRestarter>(new FakeRestarter())));
        var client = await LoggedInAsync(factory);
        await SeedAppDraftAsync(factory, "https://github.com");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/setup/apply", null)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        Assert.DoesNotContain(db.Settings, s => s.Key == "Naudit:GitHub:BaseUrl"); // Options-Default reicht
    }
}
