namespace Naudit.Infrastructure.Ui;

/// <summary>Config-Section Naudit:Ui — WebUI (Dashboard, Auth, Accounts). Opt-in:
/// Enabled=false (Default) ⇒ keine UI-Endpoints, kein Auth. Setzt die DB voraus
/// (Naudit:Db:Enabled — UI ⇒ DB, s. DependencyInjection).</summary>
public sealed class UiOptions
{
    public bool Enabled { get; set; }

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
