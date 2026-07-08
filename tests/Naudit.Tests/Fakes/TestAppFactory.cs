using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Naudit.Tests.Fakes;

/// <summary>WebApplicationFactory mit eigener SQLite-Temp-DB pro Factory-Instanz.
/// Nötig, weil die DB immer an ist: ohne das teilten sich parallel laufende
/// Testklassen die Default-DB-Datei (Lock-Flakiness).</summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbDir = Directory.CreateTempSubdirectory("naudit-test-db").FullName;
    private bool _githubBaseline = true;

    /// <summary>Schaltet die GitHub-Baseline (Token/WebhookSecret) VOR dem ersten Host-Build ab —
    /// als Methode statt Ctor-Parameter, weil xUnits IClassFixture-Aktivierung (z. B.
    /// ReviewEndpointTests) genau EINEN, parameterlos aufrufbaren Konstruktor verlangt. Noetig fuer
    /// Tests, die pruefen, dass ein Wert wirklich aus Draft/DB stammt (POST /api/setup/apply):
    /// einmal per UseSetting gesetzt, laesst sich ein Key nicht mehr "env-ungesperrt" machen — auch
    /// UseSetting(key, "") bzw. (key, null) bleibt fuer EnvOverrides ein GESETZTER (leerer) Wert.</summary>
    public TestAppFactory WithoutGitHubBaseline()
    {
        _githubBaseline = false;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Naudit:Db:ConnectionString", $"Data Source={Path.Combine(_dbDir, "naudit.db")}");

        // Baseline: Minimal-Config, mit der SetupStatus BEIDE Plattformen als "konfiguriert" sieht —
        // sonst starteten alle bestehenden WAF-Tests im Setup-Modus und die Webhook-Endpoints fehlten.
        // (appsettings.json liefert Ai:Model und GitLab:BaseUrl bereits.) Einzelne Tests
        // ueberschreiben gezielt per UseSetting; UseSetting(key, "") macht einen Key wieder "fehlend".
        builder.UseSetting("Naudit:GitLab:Token", "test-token");
        builder.UseSetting("Naudit:GitLab:WebhookSecret", "s");
        if (_githubBaseline)
        {
            builder.UseSetting("Naudit:GitHub:Token", "test-token");
            builder.UseSetting("Naudit:GitHub:WebhookSecret", "s");
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dbDir, recursive: true); } catch (IOException) { /* Windows-File-Locks: Temp bleibt */ }
    }
}
