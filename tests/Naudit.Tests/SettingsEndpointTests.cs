using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Tests.Fakes;
using Naudit.Web;
using Xunit;

namespace Naudit.Tests;

public class SettingsEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public SettingsEndpointTests(TestAppFactory factory) => _factory = factory;

    private sealed class FakeRestarter : IAppRestarter
    {
        public int RestartCalls;
        public bool RestartPending { get; private set; }
        public void RequestRestart() => RestartCalls++;
        public void MarkRestartPending() => RestartPending = true;
    }

    private (HttpClient Client, FakeRestarter Restarter) CreateLoggedInAdmin()
    {
        var restarter = new FakeRestarter();
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Ui:Admin:Username", "admin");
            b.UseSetting("Naudit:Ui:Admin:InitialPassword", "pw-123456");
            b.ConfigureServices(s => s.AddSingleton<IAppRestarter>(restarter));
        }).CreateClient();
        var login = client.PostAsJsonAsync("/auth/login", new { username = "admin", password = "pw-123456" }).Result;
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, restarter);
    }

    [Fact]
    public async Task Get_liefertKatalog_ohneSecretWerte()
    {
        var (client, _) = CreateLoggedInAdmin();
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/settings"));
        var settings = doc.RootElement.GetProperty("settings").EnumerateArray().ToList();
        Assert.Contains(settings, s => s.GetProperty("key").GetString() == "Naudit:Ai:Provider");
        // Secrets: value ist IMMER null, egal ob gesetzt.
        Assert.All(settings.Where(s => s.GetProperty("isSecret").GetBoolean()),
            s => Assert.Equal(JsonValueKind.Null, s.GetProperty("value").ValueKind));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("recoveryError").ValueKind);
    }

    [Fact]
    public async Task Put_speichertWert_undGetZeigtIhnAlsDbQuelle()
    {
        var (client, restarter) = CreateLoggedInAdmin();
        var res = await client.PutAsJsonAsync("/api/settings", new
        {
            changes = new[] { new { key = "Naudit:Ai:Model", value = (string?)"claude-sonnet-5" } },
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(restarter.RestartPending);

        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/settings"));
        var model = doc.RootElement.GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "Naudit:Ai:Model");
        Assert.Equal("db", model.GetProperty("source").GetString());
        Assert.True(model.GetProperty("isSet").GetBoolean());
        // GET zeigt hier noch den ALTEN effektiven Wert (IConfiguration lädt DB erst beim Neustart) —
        // die UI kommuniziert das über restartPending; deshalb kein Assert auf value.
    }

    [Fact]
    public async Task Put_secret_wirdGespeichertAberNieZurueckgegeben()
    {
        var (client, _) = CreateLoggedInAdmin();
        var res = await client.PutAsJsonAsync("/api/settings", new
        {
            changes = new[] { new { key = "Naudit:Ai:ApiKey", value = (string?)"sk-geheim" } },
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await client.GetStringAsync("/api/settings");
        Assert.DoesNotContain("sk-geheim", body);
    }

    [Fact]
    public async Task Put_unbekannterKey_gibt400()
    {
        var (client, _) = CreateLoggedInAdmin();
        var res = await client.PutAsJsonAsync("/api/settings", new
        {
            changes = new[] { new { key = "Naudit:Db:ConnectionString", value = (string?)"x" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Restart_ruftRestarter_undGibt204()
    {
        var (client, restarter) = CreateLoggedInAdmin();
        var res = await client.PostAsync("/api/settings/restart", null);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal(1, restarter.RestartCalls);
    }

    [Fact]
    public async Task NichtAdmin_bekommt401Oder403()
    {
        var client = _factory.CreateClient(); // nicht eingeloggt
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/settings")).StatusCode);
    }
}
