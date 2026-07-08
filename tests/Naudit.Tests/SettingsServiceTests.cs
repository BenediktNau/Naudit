using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

/// <summary>SettingsService: Katalog-Whitelist, Secret-Verschlüsselung, Upsert/Remove.</summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly NauditDbContext _db;
    private readonly SettingsService _service;

    public SettingsServiceTests()
    {
        _conn.Open();
        _db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _service = new SettingsService(_db, new EphemeralDataProtectionProvider());
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    [Fact]
    public async Task SetAsync_plainKey_speichertKlartextUndUpserted()
    {
        await _service.SetAsync("Naudit:Ai:Provider", "Anthropic");
        await _service.SetAsync("Naudit:Ai:Provider", "Ollama"); // Upsert, kein Duplikat
        var row = await _db.Settings.SingleAsync(s => s.Key == "Naudit:Ai:Provider");
        Assert.Equal("Ollama", row.Value);
        Assert.False(row.IsSecret);
    }

    [Fact]
    public async Task SetAsync_secretKey_speichertNieKlartext()
    {
        await _service.SetAsync("Naudit:Ai:ApiKey", "sk-super-geheim");
        var row = await _db.Settings.SingleAsync(s => s.Key == "Naudit:Ai:ApiKey");
        Assert.True(row.IsSecret);
        Assert.DoesNotContain("sk-super-geheim", row.Value);
    }

    [Fact]
    public async Task SetAsync_unbekannterKey_wirft()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetAsync("Naudit:Nope", "x"));
    }

    [Fact]
    public async Task RemoveAsync_undGetSetKeys()
    {
        await _service.SetAsync("Naudit:Ai:Model", "sonnet");
        Assert.Contains("Naudit:Ai:Model", await _service.GetSetKeysAsync());
        Assert.True(await _service.RemoveAsync("Naudit:Ai:Model"));
        Assert.False(await _service.RemoveAsync("Naudit:Ai:Model")); // schon weg
        Assert.Empty(await _service.GetSetKeysAsync());
    }

    [Fact]
    public void Catalog_kenntKernKeys_mitKorrektemSecretFlag()
    {
        Assert.True(SettingsCatalog.TryGet("Naudit:Ai:ApiKey", out var apiKey));
        Assert.True(apiKey.IsSecret);
        Assert.True(SettingsCatalog.TryGet("naudit:git:platform", out var platform)); // case-insensitive
        Assert.False(platform.IsSecret);
        Assert.True(SettingsCatalog.TryGet("Naudit:GitHub:App:PrivateKey", out var pem));
        Assert.True(pem.IsSecret);
        Assert.True(SettingsCatalog.TryGet("Naudit:AccessGate:Mode", out _));
        Assert.False(SettingsCatalog.TryGet("Naudit:Db:ConnectionString", out _)); // Bootstrap-Key: nie DB-verwaltet
    }
}
