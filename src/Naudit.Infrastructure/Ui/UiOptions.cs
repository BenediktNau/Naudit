namespace Naudit.Infrastructure.Ui;

/// <summary>DB-Backend der Persistenz. Ein gemeinsames Schema/eine Migration für beide
/// (die InitialUi-Migration ist provider-neutral gehalten).</summary>
public enum UiDbProvider { Sqlite, Postgres }

/// <summary>Config-Section Naudit:Ui — WebUI, Zugangsschranke und Persistenz. Alles opt-in:
/// Enabled=false (Default) ⇒ exakt heutiges Verhalten (kein Gate, keine DB, keine UI-Endpoints).</summary>
public sealed class UiOptions
{
    public bool Enabled { get; set; }

    /// <summary>DB-Backend: SQLite (Default, /data-Volume) oder Postgres (externe DB).</summary>
    public UiDbProvider DbProvider { get; set; } = UiDbProvider.Sqlite;

    /// <summary>Connection-String für das gewählte Backend:
    /// SQLite <c>Data Source=/data/naudit.db</c> (Default; /data liegt auf einem Volume) bzw.
    /// Postgres <c>Host=…;Database=…;Username=…;Password=…</c>.</summary>
    public string Db { get; set; } = "Data Source=/data/naudit.db";

    /// <summary>Seed-Admin: wird beim Start angelegt, wenn die Accounts-Tabelle leer ist.</summary>
    public SeedAdminOptions Admin { get; set; } = new();

    /// <summary>Usernames, die beim (externen) Sign-in automatisch Admin werden.</summary>
    public List<string> Admins { get; set; } = new();

    public UiAuthOptions Auth { get; set; } = new();
}

public sealed class SeedAdminOptions
{
    public string Username { get; set; } = "";
    public string InitialPassword { get; set; } = "";
}

public sealed class UiAuthOptions
{
    public OAuthProviderOptions GitHub { get; set; } = new();
    public OidcProviderOptions Oidc { get; set; } = new();
}

public sealed class OAuthProviderOptions
{
    public bool Enabled { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public sealed class OidcProviderOptions
{
    public bool Enabled { get; set; }
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}
