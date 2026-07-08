using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Setup;
using Xunit;

namespace Naudit.Tests;

/// <summary>Wizard-Draft: eine Zeile (Id=1), JSON-Blob DP-verschlüsselt at rest,
/// nicht entschlüsselbar (Keyring weg) ⇒ null statt Crash.</summary>
public sealed class SetupDraftServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly NauditDbContext _db;

    public SetupDraftServiceTests()
    {
        _conn.Open();
        _db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    [Fact]
    public async Task SaveLoad_roundtrip_undNieKlartextInDerZeile()
    {
        var service = new SetupDraftService(_db, new EphemeralDataProtectionProvider());
        await service.SaveAsync("""{"GitToken":"glpat-geheim"}""");
        await service.SaveAsync("""{"GitToken":"glpat-geheim-2"}"""); // Upsert, kein Duplikat

        Assert.Equal("""{"GitToken":"glpat-geheim-2"}""", await service.LoadAsync());
        var row = await _db.SetupDrafts.SingleAsync();
        Assert.Equal(1, row.Id);
        Assert.DoesNotContain("glpat-geheim", row.Json);
    }

    [Fact]
    public async Task Clear_entferntDenDraft()
    {
        var service = new SetupDraftService(_db, new EphemeralDataProtectionProvider());
        await service.SaveAsync("{}");
        await service.ClearAsync();
        Assert.Null(await service.LoadAsync());
        await service.ClearAsync(); // idempotent
    }

    [Fact]
    public async Task Load_nichtEntschluesselbar_gibtNull()
    {
        // Zwei getrennte Ephemeral-Provider = Keyring weg: Load darf nicht werfen.
        var writer = new SetupDraftService(_db, new EphemeralDataProtectionProvider());
        await writer.SaveAsync("{}");
        var reader = new SetupDraftService(_db, new EphemeralDataProtectionProvider());
        Assert.Null(await reader.LoadAsync());
    }
}
