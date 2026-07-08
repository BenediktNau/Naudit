namespace Naudit.Infrastructure.Ui;

/// <summary>Config-Section Naudit:Ui — WebUI-Belange (Seed-Admin, Admin-Liste, Sign-in-Provider).
/// Die UI selbst ist immer an (sie ist die Konfigurationsoberfläche).</summary>
public sealed class UiOptions
{
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
