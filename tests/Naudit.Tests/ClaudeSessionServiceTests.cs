using Microsoft.AspNetCore.DataProtection;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Ui;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ClaudeSessionServiceTests
{
    private static AccountEntity Account(TestDb db, string username = "alice",
        AccountProvider provider = AccountProvider.GitHub, AccountStatus status = AccountStatus.Active)
    {
        var a = new AccountEntity { Username = username, Provider = provider, Status = status, CreatedAt = DateTime.UtcNow };
        db.Context.Accounts.Add(a);
        db.Context.SaveChanges();
        return a;
    }

    private static ClaudeSessionService Service(TestDb db) =>
        new(db.Context, new EphemeralDataProtectionProvider());

    [Fact]
    public async Task SetToken_encryptsAtRest_andRoundTrips()
    {
        using var db = new TestDb();
        var acct = Account(db);
        var svc = Service(db);

        await svc.SetTokenAsync(acct.Id, "sk-ant-oat01-geheim", null);

        Assert.NotNull(acct.ClaudeSessionToken);
        Assert.DoesNotContain("geheim", acct.ClaudeSessionToken);      // verschlüsselt at rest
        Assert.NotNull(acct.ClaudeSessionUpdatedAtUtc);
        Assert.Equal("sk-ant-oat01-geheim", svc.DecryptToken(acct));   // Roundtrip
    }

    [Fact]
    public async Task SetToken_gitHubAccount_autoFillsGitAuthorLogin_lowercased()
    {
        using var db = new TestDb();
        var acct = Account(db, username: "Alice", provider: AccountProvider.GitHub);

        await Service(db).SetTokenAsync(acct.Id, "tok", null);

        Assert.Equal("alice", acct.GitAuthorLogin);
    }

    [Fact]
    public async Task SetToken_explicitLogin_winsOverAutoFill()
    {
        using var db = new TestDb();
        var acct = Account(db);

        await Service(db).SetTokenAsync(acct.Id, "tok", "Bob-GitLab");

        Assert.Equal("bob-gitlab", acct.GitAuthorLogin);
    }

    [Fact]
    public async Task FindByAuthorLogin_matchesCaseInsensitive_onlyActiveWithToken()
    {
        using var db = new TestDb();
        var active = Account(db, "alice");
        var pending = Account(db, "bob", status: AccountStatus.Pending);
        var svc = Service(db);
        await svc.SetTokenAsync(active.Id, "tok", "alice");
        await svc.SetTokenAsync(pending.Id, "tok", "bob");

        Assert.Equal(active.Id, (await svc.FindByAuthorLoginAsync("ALICE"))!.Id);
        Assert.Null(await svc.FindByAuthorLoginAsync("bob"));       // pending zählt nicht
        Assert.Null(await svc.FindByAuthorLoginAsync("unbekannt"));
    }

    [Fact]
    public async Task RemoveToken_clearsTokenAndTimestamp_keepsLogin()
    {
        using var db = new TestDb();
        var acct = Account(db);
        var svc = Service(db);
        await svc.SetTokenAsync(acct.Id, "tok", "alice");

        await svc.RemoveTokenAsync(acct.Id);

        Assert.Null(acct.ClaudeSessionToken);
        Assert.Null(acct.ClaudeSessionUpdatedAtUtc);
        Assert.Equal("alice", acct.GitAuthorLogin);   // Login bleibt — Nutzer will nur den Token weg
    }

    [Fact]
    public void DecryptToken_undecryptable_returnsNull()
    {
        using var db = new TestDb();
        var acct = Account(db);
        acct.ClaudeSessionToken = "CfDJ8-kaputt-nicht-entschluesselbar";
        db.Context.SaveChanges();

        // Keyring weg / fremder Ciphertext ⇒ null statt Crash (Semantik wie DbSettingsLoader).
        Assert.Null(Service(db).DecryptToken(acct));
    }
}
