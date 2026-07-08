using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Naudit.Infrastructure.Data;

/// <summary>DB-Backend der Persistenz. Ein gemeinsames Schema/eine Migrationskette für beide
/// (die Migrationen sind provider-neutral handgepflegt).</summary>
public enum DbProvider { Sqlite, Postgres }

/// <summary>Config-Section Naudit:Db — Naudits Persistenz als eigenständiger Belang
/// (Zugangsschranke, Audit-Log, Accounts, Data-Protection-Keys), von der UI entkoppelt.
/// Enabled ist EXPLIZIT (Default false) statt aus dem ConnectionString abgeleitet,
/// weil der ConnectionString einen Default-Wert hat.</summary>
public sealed class DatabaseOptions
{
    public bool Enabled { get; set; }

    /// <summary>DB-Backend: SQLite (Default, /data-Volume) oder Postgres (externe DB).</summary>
    public DbProvider Provider { get; set; } = DbProvider.Sqlite;

    /// <summary>Connection-String für das gewählte Backend:
    /// SQLite <c>Data Source=/data/naudit.db</c> (Default; /data liegt auf einem Volume) bzw.
    /// Postgres <c>Host=…;Database=…;Username=…;Password=…</c>.</summary>
    public string ConnectionString { get; set; } = "Data Source=/data/naudit.db";

    /// <summary>Die EINE Provider-Weiche für DbContext-Konfiguration — von DI und
    /// DbSettingsLoader gemeinsam genutzt, damit Bootstrap und Laufzeit nie divergieren.</summary>
    public static void ConfigureDbContext(DbContextOptionsBuilder builder, DatabaseOptions options)
    {
        switch (options.Provider)
        {
            case DbProvider.Postgres:
                builder.UseNpgsql(options.ConnectionString);
                // Snapshot ist SQLite-geprägt — konventionsbedingter Diff auf Postgres ist gutartig.
                builder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
                break;
            default:
                builder.UseSqlite(options.ConnectionString);
                break;
        }
    }
}
