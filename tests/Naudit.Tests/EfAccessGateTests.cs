using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Ui;
using Xunit;

namespace Naudit.Tests;

public class EfAccessGateTests
{
    private static NauditDbContext NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"naudit-test-{Guid.NewGuid():N}.db");
        var db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        db.Database.Migrate();
        return db;
    }

    private static async Task AddAccount(NauditDbContext db, string login, AccountStatus status)
    {
        var a = new AccountEntity { Username = login, Provider = AccountProvider.Local, Status = status, CreatedAt = DateTime.UtcNow };
        a.GitHubLinks.Add(new GitHubLinkEntity { Login = login.ToLowerInvariant() });
        db.Accounts.Add(a);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Allowed_forActiveAccountOwner_caseInsensitive()
    {
        await using var db = NewDb();
        await AddAccount(db, "BenediktNau", AccountStatus.Active);
        var gate = new EfAccessGate(db);

        Assert.True(await gate.IsAllowedAsync("BenediktNau/Naudit"));
        Assert.True(await gate.IsAllowedAsync("benediktnau/andere-repo"));
    }

    [Fact]
    public async Task Denied_forPendingAccount_unknownOwner_andEmptyProjectId()
    {
        await using var db = NewDb();
        await AddAccount(db, "pending-user", AccountStatus.Pending);
        var gate = new EfAccessGate(db);

        Assert.False(await gate.IsAllowedAsync("pending-user/repo"));
        Assert.False(await gate.IsAllowedAsync("fremd/repo"));
        Assert.False(await gate.IsAllowedAsync(""));
    }

    [Fact]
    public async Task GitLabNumericProjectId_matchesWholeValueAsLink()
    {
        await using var db = NewDb();
        await AddAccount(db, "4711", AccountStatus.Active); // GitLab: Link-Wert = ProjectId selbst
        var gate = new EfAccessGate(db);
        Assert.True(await gate.IsAllowedAsync("4711"));
    }
}
