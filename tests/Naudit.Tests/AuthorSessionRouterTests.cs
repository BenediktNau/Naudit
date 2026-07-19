using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Ui;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class AuthorSessionRouterTests
{
    private static readonly ReviewRequest Request = new("o/r", 1, "T", "alice");

    private sealed class FixedAuthorResolver(string? login) : IAuthorLoginResolver
    {
        public Task<string?> ResolveAsync(ReviewRequest request, CancellationToken ct = default)
            => Task.FromResult(login);
    }

    private static string Envelope(string result)
        => JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result });

    private static async Task<int> SeedAccountWithToken(TestDb db, ClaudeSessionService svc, string login = "alice")
    {
        var a = new AccountEntity { Username = login, Provider = AccountProvider.GitHub, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow };
        db.Context.Accounts.Add(a);
        await db.Context.SaveChangesAsync();
        await svc.SetTokenAsync(a.Id, "tok-123", login);
        return a.Id;
    }

    private static AuthorSessionRouter Router(ClaudeSessionService sessions, IAuthorLoginResolver resolver,
        SessionHealthRegistry health, IProcessRunner runner, Microsoft.Extensions.AI.IChatClient global,
        AuthorSessionsOptions? options = null)
    {
        var selectionFactory = new SessionSelectionFactory(options ?? new AuthorSessionsOptions(),
            new AiOptions { Provider = AiProvider.Ollama, Model = "egal" }, global,
            new InProcessSessionRunnerFactory(runner), health, NullLoggerFactory.Instance);
        return new(sessions, resolver, health, selectionFactory, NullLogger<AuthorSessionRouter>.Instance);
    }

    [Fact]
    public async Task NoAuthorLogin_returnsGlobalClient()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var global = new FakeChatClient("GLOBAL");
        var router = Router(svc, new FixedAuthorResolver(null), new SessionHealthRegistry(),
            new StubProcessRunner(_ => throw new InvalidOperationException("kein CLI-Lauf erwartet")), global);

        var selection = await router.SelectAsync(Request);

        Assert.Same(global, selection.Client);
        Assert.Null(selection.UsedSessionAccountId());
    }

    [Fact]
    public async Task NoAccountWithToken_returnsGlobalClient()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var global = new FakeChatClient("GLOBAL");
        var router = Router(svc, new FixedAuthorResolver("alice"), new SessionHealthRegistry(),
            new StubProcessRunner(_ => throw new InvalidOperationException()), global);

        Assert.Same(global, (await router.SelectAsync(Request)).Client);
    }

    [Fact]
    public async Task CoolingDown_returnsGlobalClient()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var health = new SessionHealthRegistry();
        var accountId = await SeedAccountWithToken(db, svc);
        health.MarkFailure(accountId, TimeSpan.FromMinutes(30));
        var global = new FakeChatClient("GLOBAL");
        var router = Router(svc, new FixedAuthorResolver("alice"), health,
            new StubProcessRunner(_ => throw new InvalidOperationException()), global);

        Assert.Same(global, (await router.SelectAsync(Request)).Client);
    }

    [Fact]
    public async Task UndecryptableToken_returnsGlobalClient()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var accountId = await SeedAccountWithToken(db, svc);
        var account = await db.Context.Accounts.FindAsync(accountId);
        account!.ClaudeSessionToken = "CfDJ8-kaputt-nicht-entschluesselbar"; // fremder Ciphertext, nicht entschlüsselbar
        await db.Context.SaveChangesAsync();
        var global = new FakeChatClient("GLOBAL");
        var router = Router(svc, new FixedAuthorResolver("alice"), new SessionHealthRegistry(),
            new StubProcessRunner(_ => throw new InvalidOperationException("kein CLI-Lauf erwartet")), global);

        var selection = await router.SelectAsync(Request);

        Assert.Same(global, selection.Client);
        Assert.Null(selection.UsedSessionAccountId());
    }

    [Fact]
    public async Task HappyPath_runsCliWithAuthorToken_andAttributesSession()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var accountId = await SeedAccountWithToken(db, svc);
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("{\"summary\":\"ok\"}"), ""));
        var router = Router(svc, new FixedAuthorResolver("alice"), new SessionHealthRegistry(), stub, new FakeChatClient("GLOBAL"));

        var selection = await router.SelectAsync(Request);
        var response = await selection.Client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "diff")]);

        Assert.Contains("ok", response.Text);
        Assert.Equal(accountId, selection.UsedSessionAccountId());          // Autor-Session hat geantwortet
        Assert.Equal("tok-123", stub.LastSpec!.Environment!["CLAUDE_CODE_OAUTH_TOKEN"]);
        var args = stub.LastSpec.Arguments.ToList();
        Assert.Equal("sonnet", args[args.IndexOf("--model") + 1]);          // AuthorSessions:Model, nicht Naudit:Ai:Model
    }

    [Fact]
    public async Task AuthorRunFails_fallsBackToGlobal_andSetsCooldown()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var accountId = await SeedAccountWithToken(db, svc);
        var health = new SessionHealthRegistry();
        var stub = new StubProcessRunner(_ => throw new InvalidOperationException("rate limit"));
        var global = new FakeChatClient("GLOBAL");
        var router = Router(svc, new FixedAuthorResolver("alice"), health, stub, global);

        var selection = await router.SelectAsync(Request);
        var response = await selection.Client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "diff")]);

        Assert.Equal("GLOBAL", response.Text);
        Assert.Null(selection.UsedSessionAccountId());
        Assert.True(health.IsCoolingDown(accountId));
    }

    private sealed class ThrowingAuthorResolver : IAuthorLoginResolver
    {
        public Task<string?> ResolveAsync(ReviewRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("Resolver-Backend nicht erreichbar");
    }

    [Fact]
    public async Task ResolverThrows_returnsGlobalClient_failOpen()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var global = new FakeChatClient("GLOBAL");
        var router = Router(svc, new ThrowingAuthorResolver(), new SessionHealthRegistry(),
            new StubProcessRunner(_ => throw new InvalidOperationException("kein CLI-Lauf erwartet")), global);

        var selection = await router.SelectAsync(Request);

        Assert.Same(global, selection.Client);
        Assert.Null(selection.UsedSessionAccountId());
    }
}
