namespace Naudit.Infrastructure.Settings;

/// <summary>Ein DB-verwaltbarer Konfigurationswert. IsSecret steuert Verschlüsselung
/// und Write-only-Verhalten der Settings-API.</summary>
public sealed record SettingDefinition(string Key, bool IsSecret);

/// <summary>Whitelist der DB-verwaltbaren Keys. Bootstrap-Keys (Naudit:Db:*, ForwardedHeaders,
/// Ports) fehlen hier bewusst — sie müssen vor dem DB-Zugriff bekannt sein und bleiben env-only.
/// Listen-Keys (ProjectTokens, Ui:Admins) bleiben vorerst ebenfalls env-only (Index-Keys passen
/// schlecht in ein Key/Value-Formular).</summary>
public static class SettingsCatalog
{
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new("Naudit:PublicBaseUrl", false),
        new("Naudit:Git:Platform", false),
        new("Naudit:GitLab:BaseUrl", false),
        new("Naudit:GitLab:Token", true),
        new("Naudit:GitLab:WebhookSecret", true),
        new("Naudit:GitLab:PostVerdict", false),
        new("Naudit:GitHub:BaseUrl", false),
        new("Naudit:GitHub:Token", true),
        new("Naudit:GitHub:WebhookSecret", true),
        new("Naudit:GitHub:Auth", false),
        new("Naudit:GitHub:App:AppId", false),
        new("Naudit:GitHub:App:PrivateKey", true),
        new("Naudit:GitHub:App:InstallationId", false),
        new("Naudit:GitHub:PostVerdict", false),
        new("Naudit:Ai:Provider", false),
        new("Naudit:Ai:Model", false),
        new("Naudit:Ai:Endpoint", false),
        new("Naudit:Ai:ApiKey", true),
        new("Naudit:Ai:SessionRouting", false),
        new("Naudit:Ai:AuthorSessions:CooldownMinutes", false),
        new("Naudit:Ai:AuthorSessions:Model", false),
        new("Naudit:Review:SystemPrompt", false),
        new("Naudit:Review:Gate:MinSeverity", false),
        new("Naudit:Review:Gate:MinConfidence", false),
        new("Naudit:Review:MaxRoundtrips", false),
        new("Naudit:AccessGate:Mode", false),
        new("Naudit:Ui:Auth:GitHub:Enabled", false),
        new("Naudit:Ui:Auth:GitHub:ClientId", false),
        new("Naudit:Ui:Auth:GitHub:ClientSecret", true),
        new("Naudit:Ui:Auth:Oidc:Enabled", false),
        new("Naudit:Ui:Auth:Oidc:Authority", false),
        new("Naudit:Ui:Auth:Oidc:ClientId", false),
        new("Naudit:Ui:Auth:Oidc:ClientSecret", true),
    ];

    private static readonly Dictionary<string, SettingDefinition> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string key, out SettingDefinition definition) =>
        ByKey.TryGetValue(key, out definition!);
}
