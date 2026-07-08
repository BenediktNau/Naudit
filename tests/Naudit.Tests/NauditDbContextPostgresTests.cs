using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Naudit.Infrastructure.Data;
using Xunit;

namespace Naudit.Tests;

/// <summary>Opt-in-Gegenprobe für Postgres: beweist, dass dieselbe (provider-neutrale)
/// InitialUi-Migration auch auf Postgres greift, die Identity-Spalten dort auto-inkrementieren
/// und ein Aggregat roundtrippt. Läuft nur, wenn NAUDIT_TEST_POSTGRES (ein Npgsql-ConnectionString)
/// gesetzt ist — sonst No-Op, damit CI ohne laufenden Postgres grün bleibt. Lokal z. B.:
///   docker run -d -e POSTGRES_PASSWORD=naudit -e POSTGRES_DB=naudit -p 55432:5432 postgres:17-alpine
///   NAUDIT_TEST_POSTGRES="Host=localhost;Port=55432;Database=naudit;Username=postgres;Password=naudit" \
///     dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter NauditDbContextPostgresTests
/// </summary>
public class NauditDbContextPostgresTests
{
    private static string? Conn => Environment.GetEnvironmentVariable("NAUDIT_TEST_POSTGRES");

    private static NauditDbContext CreateCleanDb(string conn)
    {
        var opts = new DbContextOptionsBuilder<NauditDbContext>()
            .UseNpgsql(conn)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)) // s. DependencyInjection.cs
            .Options;
        var db = new NauditDbContext(opts);
        db.Database.EnsureDeleted(); // sauberer Start bei wiederholten Läufen
        db.Database.Migrate();       // dieselbe committete Migration muss auf Postgres greifen
        return db;
    }

    [Fact]
    public async Task Migrate_onPostgres_createsSchema_andRoundtripsAggregate()
    {
        var conn = Conn;
        if (string.IsNullOrWhiteSpace(conn)) return; // ohne Postgres-Env: übersprungen

        await using var db = CreateCleanDb(conn);

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

        Assert.True(account.Id > 0); // Identity/Auto-Increment muss auf Postgres greifen

        // Neue DataProtectionKeys-Tabelle (Migration AddDataProtectionKeys) muss auf Postgres greifen.
        db.DataProtectionKeys.Add(new DataProtectionKey { FriendlyName = "key-1", Xml = "<key id=\"1\" />" });
        await db.SaveChangesAsync();
        Assert.Equal("<key id=\"1\" />", (await db.DataProtectionKeys.SingleAsync()).Xml);

        var loaded = await db.Projects.Include(p => p.Reviews).ThenInclude(r => r.Findings).SingleAsync();
        Assert.Single(loaded.Reviews);
        Assert.Single(loaded.Reviews[0].Findings);
        Assert.Equal("benediktnau", (await db.GitHubLinks.SingleAsync()).Login);
    }

    [Fact]
    public async Task Accounts_usernameIsUnique_onPostgres()
    {
        var conn = Conn;
        if (string.IsNullOrWhiteSpace(conn)) return;

        await using var db = CreateCleanDb(conn);
        db.Accounts.Add(new AccountEntity { Username = "dup", Provider = AccountProvider.Local, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.Accounts.Add(new AccountEntity { Username = "dup", Provider = AccountProvider.Local, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
