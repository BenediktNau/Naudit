using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Naudit.Infrastructure.Data;

/// <summary>DB-Backend der Persistenz. Ein gemeinsames Schema/eine Migrationskette für beide
/// (die Migrationen sind provider-neutral handgepflegt).</summary>
public enum DbProvider { Sqlite, Postgres }

/// <summary>Config-Section Naudit:Db — Naudits Persistenz (Settings, Zugangsschranke, Audit-Log,
/// Accounts, Data-Protection-Keys). Die DB ist PFLICHT (Bootstrap-Keys, env-only): SQLite ist der
/// Zero-Config-Default (relativer Pfad für den Binary-Fall; das Dockerfile setzt /data/naudit.db).</summary>
public sealed class DatabaseOptions
{
    /// <summary>DB-Backend: SQLite (Default) oder Postgres (externe DB).</summary>
    public DbProvider Provider { get; set; } = DbProvider.Sqlite;

    /// <summary>Connection-String für das gewählte Backend:
    /// SQLite <c>Data Source=data/naudit.db</c> (Default; relativer Pfad, das Dockerfile setzt
    /// <c>/data/naudit.db</c> auf einem Volume) bzw.
    /// Postgres <c>Host=…;Database=…;Username=…;Password=…</c>.</summary>
    public string ConnectionString { get; set; } = "Data Source=data/naudit.db";

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
