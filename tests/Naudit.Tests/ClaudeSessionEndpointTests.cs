using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure.Process;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ClaudeSessionEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public ClaudeSessionEndpointTests(TestAppFactory factory) => _factory = factory;

    private static string Envelope(string result)
        => JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result });

    private async Task<HttpClient> LoggedInApp(Func<ProcessSpec, ProcessResult>? cliResponder = null)
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-cs-{Guid.NewGuid():N}.db")}";
        var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Db:ConnectionString", db);
            b.UseSetting("Naudit:Ui:Admin:Username", "root");
            b.UseSetting("Naudit:Ui:Admin:InitialPassword", "passwort123");
            if (cliResponder is not null)
                b.ConfigureTestServices(s => s.AddSingleton<IProcessRunner>(new StubProcessRunner(cliResponder)));
        });
        var client = factory.CreateDefaultClient(new CookieContainerHandler());
        await client.PostAsJsonAsync("/auth/login", new { username = "root", password = "passwort123" });
        return client;
    }

    [Fact]
    public async Task Get_withoutLogin_returns401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/me/claude-session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PutGetDelete_roundTrip_neverEchoesToken()
    {
        var client = await LoggedInApp();

        var before = await client.GetFromJsonAsync<JsonElement>("/api/me/claude-session");
        Assert.False(before.GetProperty("configured").GetBoolean());

        var put = await client.PutAsJsonAsync("/api/me/claude-session",
            new { token = "sk-ant-oat01-geheim", gitAuthorLogin = "Alice" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/me/claude-session");
        Assert.True(after.GetProperty("configured").GetBoolean());
        Assert.Equal("alice", after.GetProperty("gitAuthorLogin").GetString());
        Assert.DoesNotContain("geheim", after.ToString());        // Token verlässt den Server nie

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/me/claude-session")).StatusCode);
        var cleared = await client.GetFromJsonAsync<JsonElement>("/api/me/claude-session");
        Assert.False(cleared.GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Put_blankTokenWithoutStoredToken_returns400()
    {
        var client = await LoggedInApp();
        var put = await client.PutAsJsonAsync("/api/me/claude-session", new { token = "", gitAuthorLogin = "alice" });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Put_blankToken_keepsStoredToken_updatesLogin()
    {
        var client = await LoggedInApp();
        await client.PutAsJsonAsync("/api/me/claude-session", new { token = "tok", gitAuthorLogin = "alice" });

        var put = await client.PutAsJsonAsync("/api/me/claude-session", new { token = "", gitAuthorLogin = "Bob" });

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var state = await client.GetFromJsonAsync<JsonElement>("/api/me/claude-session");
        Assert.True(state.GetProperty("configured").GetBoolean());   // Token blieb erhalten
        Assert.Equal("bob", state.GetProperty("gitAuthorLogin").GetString());
    }

    [Fact]
    public async Task Put_shareInPool_isPersisted_andReturnedByGet()
    {
        var client = await LoggedInApp();
        await client.PutAsJsonAsync("/api/me/claude-session", new { token = "tok", gitAuthorLogin = "alice" });

        var put = await client.PutAsJsonAsync("/api/me/claude-session", new { shareInPool = true });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/me/claude-session");
        Assert.True(after.GetProperty("shareInPool").GetBoolean());
    }

    [Fact]
    public async Task Put_shareInPoolOnly_noStoredToken_returns204()
    {
        var client = await LoggedInApp();

        // Reines Opt-in-Toggle auf frischem Account (kein Token, kein Login) ⇒ 204, KEIN „token required".
        var put = await client.PutAsJsonAsync("/api/me/claude-session", new { shareInPool = true });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/me/claude-session");
        Assert.True(after.GetProperty("shareInPool").GetBoolean());
        Assert.False(after.GetProperty("configured").GetBoolean());   // Opt-in gesetzt, aber weiterhin kein Token
    }

    [Fact]
    public async Task Put_shareInPoolWithLogin_noStoredToken_returns204()
    {
        var client = await LoggedInApp();

        // Opt-in + Login (z. B. GitHub-Login vorausgefüllt), aber noch kein Token ⇒ trotzdem 204,
        // KEIN „token required" (Regression: der Login-Wert darf den Opt-in-Erfolg nicht verdecken).
        var put = await client.PutAsJsonAsync("/api/me/claude-session", new { shareInPool = true, gitAuthorLogin = "someone" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/me/claude-session");
        Assert.True(after.GetProperty("shareInPool").GetBoolean());
        Assert.False(after.GetProperty("configured").GetBoolean());   // Opt-in gesetzt, aber weiterhin kein Token
    }

    [Fact]
    public async Task Test_runsCliWithStoredToken_andReportsOk()
    {
        ProcessSpec? seen = null;
        var client = await LoggedInApp(spec => { seen = spec; return new ProcessResult(0, Envelope("OK"), ""); });
        await client.PutAsJsonAsync("/api/me/claude-session", new { token = "tok-77", gitAuthorLogin = "alice" });

        var result = await client.PostAsync("/api/me/claude-session/test", null);
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("tok-77", seen!.Environment!["CLAUDE_CODE_OAUTH_TOKEN"]);
    }

    [Fact]
    public async Task Test_withoutToken_returns400()
    {
        var client = await LoggedInApp(_ => new ProcessResult(0, Envelope("OK"), ""));
        var result = await client.PostAsync("/api/me/claude-session/test", null);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }
}
