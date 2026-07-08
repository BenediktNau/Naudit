using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class AdminEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public AdminEndpointTests(TestAppFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminClient()
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-admin-{Guid.NewGuid():N}.db")}";
        var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitLab");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "s");
            b.UseSetting("Naudit:Db:ConnectionString", db);
            b.UseSetting("Naudit:Ui:Admin:Username", "root");
            b.UseSetting("Naudit:Ui:Admin:InitialPassword", "passwort123");
        });
        var client = factory.CreateDefaultClient(new CookieContainerHandler());
        var login = await client.PostAsJsonAsync("/auth/login", new { username = "root", password = "passwort123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    [Fact]
    public async Task Accounts_requireAdmin_401ForAnonymous()
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-admin-{Guid.NewGuid():N}.db")}";
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitLab");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "s");
            b.UseSetting("Naudit:Db:ConnectionString", db);
        }).CreateClient();
        var response = await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_listApprove_revoke_lifecycle()
    {
        var client = await AdminClient();

        // Anlegen (lokal ⇒ sofort aktiv, mit GitHub-Link)
        var create = await client.PostAsJsonAsync("/api/accounts",
            new { username = "acme-dev", password = "passwort123", isAdmin = false, gitHubLogins = new[] { "Acme-Org" } });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal("Active", created.GetProperty("status").GetString());
        Assert.Equal("acme-org", created.GetProperty("gitHubLogins")[0].GetString());

        // Liste: root + acme-dev unter approved
        var list = await client.GetFromJsonAsync<JsonElement>("/api/accounts");
        Assert.Equal(2, list.GetProperty("approved").GetArrayLength());
        Assert.Equal(0, list.GetProperty("pending").GetArrayLength());

        // Entziehen ⇒ wandert aus approved raus
        var revoke = await client.PostAsync($"/api/accounts/{id}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        list = await client.GetFromJsonAsync<JsonElement>("/api/accounts");
        Assert.Equal(1, list.GetProperty("approved").GetArrayLength());

        // Wieder freigeben
        var approve = await client.PostAsync($"/api/accounts/{id}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        // Links pflegen
        var links = await client.PutAsJsonAsync($"/api/accounts/{id}/github-links", new { logins = new[] { "acme-org", "acme-labs" } });
        Assert.Equal(HttpStatusCode.OK, links.StatusCode);

        // Unbekannte Id ⇒ 404
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/accounts/9999/approve", null)).StatusCode);

        // Duplikat ⇒ 409
        var dup = await client.PostAsJsonAsync("/api/accounts", new { username = "acme-dev", password = "passwort123" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }
}
