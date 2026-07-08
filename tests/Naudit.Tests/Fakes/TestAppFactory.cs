using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Naudit.Tests.Fakes;

/// <summary>WebApplicationFactory mit eigener SQLite-Temp-DB pro Factory-Instanz.
/// Nötig, weil die DB immer an ist: ohne das teilten sich parallel laufende
/// Testklassen die Default-DB-Datei (Lock-Flakiness).</summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbDir = Directory.CreateTempSubdirectory("naudit-test-db").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Naudit:Db:ConnectionString", $"Data Source={Path.Combine(_dbDir, "naudit.db")}");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dbDir, recursive: true); } catch (IOException) { /* Windows-File-Locks: Temp bleibt */ }
    }
}
