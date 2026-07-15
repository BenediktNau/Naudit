using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Ui;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class RoundRobinSessionRouterTests
{
    private static readonly ReviewRequest Request = new("o/r", 1, "T", "alice");

    private static string Envelope(string result)
        => JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result });

    private static async Task<int> SeedPooled(TestDb db, ClaudeSessionService svc, string login, bool optIn = true, bool withToken = true)
    {
        var a = new AccountEntity { Username = login, Provider = AccountProvider.GitHub, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow, ShareSessionInPool = optIn };
        db.Context.Accounts.Add(a);
        await db.Context.SaveChangesAsync();
        if (withToken) await svc.SetTokenAsync(a.Id, $"tok-{login}", login);
        return a.Id;
    }

    private static RoundRobinSessionRouter Router(ClaudeSessionService sessions, SessionHealthRegistry health,
        RoundRobinCursor cursor, IProcessRunner runner, IChatClient global)
    {
        var selectionFactory = new SessionSelectionFactory(new AuthorSessionsOptions(),
            new AiOptions { Provider = AiProvider.Ollama, Model = "egal" }, global, runner, health, NullLoggerFactory.Instance);
        return new(sessions, health, cursor, selectionFactory, NullLogger<RoundRobinSessionRouter>.Instance);
    }

    // Hilfsfunktion: Selection abrufen UND den Client aufrufen (damit die Attribution feststeht).
    private static async Task<int?> Pick(RoundRobinSessionRouter router, IProcessRunner _ = null!)
    {
        var sel = await router.SelectAsync(Request);
        await sel.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "diff")]);
        return sel.UsedSessionAccountId();
    }

    [Fact]
    public async Task EmptyPool_returnsGlobalClient()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var global = new FakeChatClient("GLOBAL");
        var router = Router(svc, new SessionHealthRegistry(), new RoundRobinCursor(),
            new StubProcessRunner(_ => throw new InvalidOperationException("kein CLI-Lauf erwartet")), global);

        var sel = await router.SelectAsync(Request);

        Assert.Same(global, sel.Client);
        Assert.Null(sel.UsedSessionAccountId());
    }

    [Fact]
    public async Task RotatesAcrossPooledAccounts_inIdOrder()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var id1 = await SeedPooled(db, svc, "alice");
        var id2 = await SeedPooled(db, svc, "bob");
        var id3 = await SeedPooled(db, svc, "carol");
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("{\"summary\":\"ok\"}"), ""));
        var router = Router(svc, new SessionHealthRegistry(), new RoundRobinCursor(), stub, new FakeChatClient("GLOBAL"));

        var picks = new[] { await Pick(router), await Pick(router), await Pick(router), await Pick(router) };

        Assert.Equal(new int?[] { id1, id2, id3, id1 }, picks);
    }

    [Fact]
    public async Task SkipsCoolingDownAccount()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var id1 = await SeedPooled(db, svc, "alice");
        var id2 = await SeedPooled(db, svc, "bob");
        var health = new SessionHealthRegistry();
        health.MarkFailure(id1, TimeSpan.FromMinutes(30));
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("{\"summary\":\"ok\"}"), ""));
        var router = Router(svc, health, new RoundRobinCursor(), stub, new FakeChatClient("GLOBAL"));

        Assert.Equal(id2, await Pick(router)); // alice auf Cooldown ⇒ nur bob im Pool
        Assert.Equal(id2, await Pick(router));
    }

    [Fact]
    public async Task ExcludesNonOptInAccount()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var optIn = await SeedPooled(db, svc, "alice", optIn: true);
        await SeedPooled(db, svc, "bob", optIn: false); // Token, aber kein Opt-in
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("{\"summary\":\"ok\"}"), ""));
        var router = Router(svc, new SessionHealthRegistry(), new RoundRobinCursor(), stub, new FakeChatClient("GLOBAL"));

        Assert.Equal(optIn, await Pick(router));
        Assert.Equal(optIn, await Pick(router)); // bob nie gewählt
    }

    [Fact]
    public async Task SkipsUndecryptableToken()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var good = await SeedPooled(db, svc, "alice");
        var bad = await SeedPooled(db, svc, "bob");
        var badAcct = await db.Context.Accounts.FindAsync(bad);
        badAcct!.ClaudeSessionToken = "CfDJ8-kaputt-nicht-entschluesselbar";
        await db.Context.SaveChangesAsync();
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("{\"summary\":\"ok\"}"), ""));
        var router = Router(svc, new SessionHealthRegistry(), new RoundRobinCursor(), stub, new FakeChatClient("GLOBAL"));

        // Egal wo der Cursor startet: der undekryptierbare bob wird übersprungen, alice antwortet.
        Assert.Equal(good, await Pick(router));
        Assert.Equal(good, await Pick(router));
    }

    [Fact]
    public async Task PoolQueryThrows_returnsGlobalClient_failOpen()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var global = new FakeChatClient("GLOBAL");
        var router = Router(svc, new SessionHealthRegistry(), new RoundRobinCursor(),
            new StubProcessRunner(_ => throw new InvalidOperationException("kein CLI-Lauf erwartet")), global);

        db.Context.Dispose(); // GetPoolCandidatesAsync wirft ObjectDisposedException

        var sel = await router.SelectAsync(Request);

        Assert.Same(global, sel.Client);
        Assert.Null(sel.UsedSessionAccountId());
    }
}
