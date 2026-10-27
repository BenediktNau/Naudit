using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Naudit.Web.Endpoints;
using Xunit;

namespace Naudit.Tests;

/// <summary>Sichtbarkeit über Owner-Links (LIKE-Match): Link-Werte dürfen nicht als
/// LIKE-Wildcards wirken — `_`/`%` in einem Login/Namespace sind Literale.</summary>
public class CurrentAccountVisibleProjectsTests
{
    private static NauditDbContext NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static async Task<List<string>> VisibleFor(string login, params string[] projects)
    {
        using var db = NewDb();
        var acct = new AccountEntity { Username = "u", Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow };
        acct.GitHubLinks.Add(new GitHubLinkEntity { Login = login });
        db.Accounts.Add(acct);
        foreach (var p in projects)
            db.Projects.Add(new ProjectEntity { PlatformProjectId = p, FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        return await CurrentAccount.VisibleProjects(db, acct).Select(p => p.PlatformProjectId).OrderBy(x => x).ToListAsync();
    }

    [Fact]
    public async Task OwnerLink_matchesOwnRepos_andExactId()
    {
        var visible = await VisibleFor("acme", "acme/api", "Acme/Web", "acme", "other/api", "acmeco/api");
        Assert.Equal(["Acme/Web", "acme", "acme/api"], visible);
    }

    [Fact]
    public async Task UnderscoreInLink_isLiteral_notSingleCharWildcard()
    {
        // Vorher: `my_org` + "/%" matchte als LIKE auch `myXorg/…` — fremde Projekte sichtbar.
        var visible = await VisibleFor("my_org", "my_org/repo", "myxorg/repo", "my-org/repo");
        Assert.Equal(["my_org/repo"], visible);
    }

    [Fact]
    public async Task PercentAndBackslashInLink_areLiteral()
    {
        Assert.Empty(await VisibleFor("%", "acme/api", "other/x"));
        Assert.Equal([@"a\b/repo"], await VisibleFor(@"a\b", @"a\b/repo", "axb/repo", "ab/repo"));
    }
}
