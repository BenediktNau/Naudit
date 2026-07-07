using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Ui;
using Xunit;

namespace Naudit.Tests;

public class AccountServiceTests
{
    private static NauditDbContext NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"naudit-test-{Guid.NewGuid():N}.db");
        var db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        db.Database.Migrate();
        return db;
    }

    private static AccountService NewService(NauditDbContext db, UiOptions? o = null) => new(db, o ?? new UiOptions());

    [Fact]
    public async Task CreateLocal_isActiveImmediately_andVerifiesPassword()
    {
        await using var db = NewDb();
        var svc = NewService(db);

        var created = await svc.CreateLocalAsync("bene", "sehr-geheim", isAdmin: true, ["BenediktNau"]);

        Assert.Equal(AccountStatus.Active, created.Status);
        Assert.Equal("benediktnau", Assert.Single(created.GitHubLinks).Login); // lowercased
        Assert.NotNull(await svc.VerifyPasswordAsync("bene", "sehr-geheim"));
        Assert.Null(await svc.VerifyPasswordAsync("bene", "falsch"));
    }

    [Fact]
    public async Task CreateLocal_rejectsShortPassword_andDuplicateUsername()
    {
        await using var db = NewDb();
        var svc = NewService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateLocalAsync("a", "kurz", false, []));
        await svc.CreateLocalAsync("dup", "passwort123", false, []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateLocalAsync("dup", "passwort123", false, []));
    }

    [Fact]
    public async Task MaterializeExternal_createsPendingOnce_reusesOnSecondSignIn()
    {
        await using var db = NewDb();
        var svc = NewService(db);

        var first = await svc.MaterializeExternalAsync(AccountProvider.GitHub, "12345", "mmustermann", "mmustermann");
        var second = await svc.MaterializeExternalAsync(AccountProvider.GitHub, "12345", "mmustermann", "mmustermann");

        Assert.Equal(AccountStatus.Pending, first.Status);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.Accounts.CountAsync());
        Assert.Equal("mmustermann", Assert.Single(first.GitHubLinks).Login);
    }

    [Fact]
    public async Task Accounts_providerExternalId_isUnique()
    {
        // DB-Netz gegen doppelte externe Identitäten (paralleler OAuth-Callback): ein zweiter
        // Insert mit gleichem (Provider, ExternalId) muss scheitern.
        await using var db = NewDb();
        db.Accounts.Add(new AccountEntity { Username = "a", Provider = AccountProvider.GitHub, ExternalId = "42", Status = AccountStatus.Pending, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.Accounts.Add(new AccountEntity { Username = "b", Provider = AccountProvider.GitHub, ExternalId = "42", Status = AccountStatus.Pending, CreatedAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Accounts_localWithNullExternalId_doNotCollide()
    {
        // NULL-ExternalId (lokale Accounts) gilt als verschieden — mehrere lokale Accounts sind ok.
        await using var db = NewDb();
        var svc = NewService(db);
        await svc.CreateLocalAsync("u1", "passwort123", false, []);
        await svc.CreateLocalAsync("u2", "passwort123", false, []);
        Assert.Equal(2, await db.Accounts.CountAsync());
    }

    [Fact]
    public async Task MaterializeExternal_adminListGrantsAdmin()
    {
        await using var db = NewDb();
        var svc = NewService(db, new UiOptions { Admins = ["boss"] });
        var acct = await svc.MaterializeExternalAsync(AccountProvider.Oidc, "sub-1", "boss", null);
        Assert.True(acct.IsAdmin);
        Assert.Empty(acct.GitHubLinks); // OIDC ohne GitHub-Login ⇒ kein Link
    }

    [Fact]
    public async Task SetStatus_andSetGitHubLinks_work()
    {
        await using var db = NewDb();
        var svc = NewService(db);
        var acct = await svc.MaterializeExternalAsync(AccountProvider.GitHub, "1", "u", "u");

        Assert.True(await svc.SetStatusAsync(acct.Id, AccountStatus.Active));
        Assert.True(await svc.SetGitHubLinksAsync(acct.Id, ["Acme-Org", "u"]));
        Assert.False(await svc.SetStatusAsync(9999, AccountStatus.Active));

        var links = await db.GitHubLinks.Where(l => l.AccountId == acct.Id).Select(l => l.Login).OrderBy(x => x).ToListAsync();
        Assert.Equal(["acme-org", "u"], links);
    }

    [Fact]
    public async Task Seed_createsAdmin_onlyOnEmptyTable()
    {
        await using var db = NewDb();
        var opts = new UiOptions { Admin = new SeedAdminOptions { Username = "root", InitialPassword = "passwort123" } };
        var svc = NewService(db, opts);

        await svc.SeedAsync();
        await svc.SeedAsync(); // idempotent

        var acct = await db.Accounts.SingleAsync();
        Assert.True(acct.IsAdmin);
        Assert.Equal(AccountStatus.Active, acct.Status);
        Assert.NotNull(await svc.VerifyPasswordAsync("root", "passwort123"));
    }
}
