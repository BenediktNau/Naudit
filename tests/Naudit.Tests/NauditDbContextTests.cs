using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Xunit;

namespace Naudit.Tests;

public class NauditDbContextTests
{
    private static NauditDbContext CreateDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"naudit-test-{Guid.NewGuid():N}.db");
        var opts = new DbContextOptionsBuilder<NauditDbContext>().UseSqlite($"Data Source={path}").Options;
        var db = new NauditDbContext(opts);
        db.Database.Migrate(); // committete Migration muss das Schema vollständig erzeugen
        return db;
    }

    [Fact]
    public async Task Migrate_createsSchema_andRoundtripsAggregate()
    {
        await using var db = CreateDb();

        var account = new AccountEntity { Username = "bene", Provider = AccountProvider.Local, Status = AccountStatus.Active, IsAdmin = true, CreatedAt = DateTime.UtcNow };
        account.GitHubLinks.Add(new GitHubLinkEntity { Login = "benediktnau" });
        db.Accounts.Add(account);

        var project = new ProjectEntity { PlatformProjectId = "BenediktNau/Naudit", FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow };
        project.Reviews.Add(new ReviewEntity
        {
            PrNumber = 31, Title = "T", Verdict = "approve", Summary = "s",
            InputTokens = 100, OutputTokens = 10, Model = "m", CreatedAt = DateTime.UtcNow,
            Findings = { new ReviewFindingEntity { Severity = "High", Confidence = "High", File = "a.cs", Line = 1, Text = "x" } },
        });
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var loaded = await db.Projects.Include(p => p.Reviews).ThenInclude(r => r.Findings).SingleAsync();
        Assert.Single(loaded.Reviews);
        Assert.Single(loaded.Reviews[0].Findings);
        Assert.Equal("benediktnau", (await db.GitHubLinks.SingleAsync()).Login);
    }

    [Fact]
    public async Task Accounts_usernameIsUnique()
    {
        await using var db = CreateDb();
        db.Accounts.Add(new AccountEntity { Username = "dup", Provider = AccountProvider.Local, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.Accounts.Add(new AccountEntity { Username = "dup", Provider = AccountProvider.Local, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Migrate_createsDataProtectionKeysTable_andRoundtrips()
    {
        await using var db = CreateDb();
        db.DataProtectionKeys.Add(new DataProtectionKey { FriendlyName = "key-1", Xml = "<key id=\"1\" />" });
        await db.SaveChangesAsync();
        Assert.Equal("<key id=\"1\" />", (await db.DataProtectionKeys.SingleAsync()).Xml);
    }

    [Fact]
    public async Task Settings_roundtrip_persistiertKeyValueSecretFlag()
    {
        await using var db = CreateDb();
        db.Settings.Add(new SettingEntity
        {
            Key = "Naudit:Ai:Provider",
            Value = "Anthropic",
            IsSecret = false,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        db.SetupDrafts.Add(new SetupDraftEntity { Id = 1, Json = "{}", UpdatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var setting = await db.Settings.SingleAsync(s => s.Key == "Naudit:Ai:Provider");
        Assert.Equal("Anthropic", setting.Value);
        Assert.False(setting.IsSecret);
        Assert.Equal("{}", (await db.SetupDrafts.SingleAsync(d => d.Id == 1)).Json);
    }
}
