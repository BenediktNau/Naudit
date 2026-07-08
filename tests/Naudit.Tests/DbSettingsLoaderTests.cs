using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

/// <summary>Bootstrap-Loader: migriert, liest Settings, entschlüsselt Secrets über einen
/// EIGENEN DP-Provider (gleiche DB, gleicher AppName) — nicht entschlüsselbar ⇒ Warnung statt Crash.</summary>
public sealed class DbSettingsLoaderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"naudit-loader-{Guid.NewGuid():N}", "naudit.db");
    private DatabaseOptions Options => new() { ConnectionString = $"Data Source={_dbPath}" };

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_dbPath)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Load_frischeDb_legtVerzeichnisAnUndLiefertLeer()
    {
        var result = DbSettingsLoader.Load(Options); // Verzeichnis existiert noch nicht
        Assert.Empty(result.Settings);
        Assert.Empty(result.Warnings);
        Assert.True(File.Exists(_dbPath)); // migriert & angelegt
    }

    [Fact]
    public void Load_liestKlartextUndEntschluesseltSecrets()
    {
        DbSettingsLoader.Load(Options); // DB anlegen/migrieren
        // Schreiben wie die App es tut: DP-Keyring in DERSELBEN DB (PersistKeysToDbContext).
        WriteViaService(svc =>
        {
            svc.SetAsync("Naudit:Ai:Provider", "Anthropic").GetAwaiter().GetResult();
            svc.SetAsync("Naudit:Ai:ApiKey", "sk-geheim").GetAwaiter().GetResult();
        });

        var result = DbSettingsLoader.Load(Options); // eigener, frischer DP-Provider
        Assert.Equal("Anthropic", result.Settings["Naudit:Ai:Provider"]);
        Assert.Equal("sk-geheim", result.Settings["Naudit:Ai:ApiKey"]);
    }

    [Fact]
    public void Load_kaputtesSecret_wirdUebersprungenMitWarnung()
    {
        DbSettingsLoader.Load(Options);
        using (var db = OpenContext())
        {
            db.Settings.Add(new SettingEntity
            {
                Key = "Naudit:Ai:ApiKey", Value = "kein-gueltiger-ciphertext",
                IsSecret = true, UpdatedAtUtc = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        var result = DbSettingsLoader.Load(Options);
        Assert.False(result.Settings.ContainsKey("Naudit:Ai:ApiKey"));
        Assert.Contains(result.Warnings, w => w.Contains("Naudit:Ai:ApiKey"));
    }

    private NauditDbContext OpenContext()
    {
        var b = new DbContextOptionsBuilder<NauditDbContext>();
        DatabaseOptions.ConfigureDbContext(b, Options);
        return new NauditDbContext(b.Options);
    }

    /// <summary>Baut denselben Minimal-Stack wie der Loader (DbContext + DP-Keys in der DB),
    /// um Settings so zu schreiben, wie die laufende App es täte.</summary>
    private void WriteViaService(Action<SettingsService> write)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<NauditDbContext>(b => DatabaseOptions.ConfigureDbContext(b, Options));
        services.AddDataProtection().PersistKeysToDbContext<NauditDbContext>()
            .SetApplicationName(DbSettingsLoader.DataProtectionAppName);
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        write(new SettingsService(
            scope.ServiceProvider.GetRequiredService<NauditDbContext>(),
            scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()));
    }
}
