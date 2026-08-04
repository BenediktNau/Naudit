namespace Naudit.Infrastructure.Settings;

/// <summary>Ein DB-verwaltbarer Konfigurationswert. IsSecret steuert Verschlüsselung und
/// Write-only-Verhalten der Settings-API. IsList ⇒ eine CSV-Zeile in der DB, die der
/// DbSettingsLoader zu indizierten Config-Keys expandiert. AllowedValues ⇒ die Settings-API
/// lehnt alles andere ab (ein ungültiger Wert würde den nächsten Start in den Recovery-Modus
/// zwingen).</summary>
public sealed record SettingDefinition(
    string Key,
    bool IsSecret,
    bool IsList = false,
    IReadOnlyList<string>? AllowedValues = null);

/// <summary>Whitelist der DB-verwaltbaren Keys. Bootstrap-Keys (Naudit:Db:*, ForwardedHeaders,
/// Ports) fehlen hier bewusst — sie müssen vor dem DB-Zugriff bekannt sein und bleiben env-only.
/// Listen-Keys sind über IsList möglich (CSV-Zeile ⇒ indizierte Config-Keys); ProjectTokens und
/// Ui:Admins bleiben trotzdem env-only — Zugangsdaten gehören nicht in dieselbe Maske.</summary>
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
        new("Naudit:Ai:Logging:Enabled", false),
        new("Naudit:Ai:Logging:IncludePrompts", false),
        new("Naudit:Ai:Logging:IncludeResponse", false),
        new("Naudit:Ai:Logging:Persist", false),
        new("Naudit:Ai:Logging:MaxCharsPerField", false),
        new("Naudit:Ai:AuthorSessions:CooldownMinutes", false),
        new("Naudit:Ai:AuthorSessions:Model", false),
        new("Naudit:Ai:SessionSandbox", false),
        new("Naudit:Ai:Sandbox:IdleTimeout", false),
        new("Naudit:Ai:Sandbox:MaxLiveContainers", false),
        new("Naudit:Ai:Sandbox:DockerSocketPath", false),
        new("Naudit:Ai:Sandbox:RemoveTimeout", false),
        new("Naudit:Ai:Sandbox:Image", false),
        new("Naudit:Sast:Enabled", false),
        new("Naudit:Sast:Analyzers", false, IsList: true,
            AllowedValues: ["opengrep", "betterleaks", "osv-scanner", "trivy", "dotnet-sca"]),
        new("Naudit:Sast:AnalyzerTimeout", false),
        new("Naudit:Sast:MaxFindingsPerGroup", false),
        new("Naudit:Sast:Reducer", false, AllowedValues: ["deterministic"]),
        new("Naudit:Review:SystemPrompt", false),
        new("Naudit:Review:Gate:MinSeverity", false),
        new("Naudit:Review:Gate:MinConfidence", false),
        new("Naudit:Review:Mcp:Enabled", false),
        new("Naudit:Review:Mcp:MaxIterations", false),
        new("Naudit:Review:Dast:Enabled", false),
        new("Naudit:Review:Dast:Projects", false, IsList: true),
        new("Naudit:Review:Dast:DockerfilePath", false),
        new("Naudit:Review:Dast:AppPort", false),
        new("Naudit:Review:Dast:HealthPath", false),
        new("Naudit:Review:Dast:TimeBudget", false),
        new("Naudit:Review:Dast:MemoryLimitMb", false),
        new("Naudit:Review:Dast:CpuLimit", false),
        new("Naudit:Review:Dast:PidsLimit", false),
        new("Naudit:Review:Dast:MaxContextMb", false),
        new("Naudit:Review:Dast:DockerSocketPath", false),
        new("Naudit:Review:Dast:ProbeImage", false),
        new("Naudit:Review:Dast:MaxProbeSteps", false),
        // Eigener Chat-Client NUR fuer den Probe-Loop (leer ⇒ globaler Naudit:Ai-Provider).
        // AllowedValues hier bewusst gesetzt: ein Tippfehler wuerde das Enum-Binding sprengen
        // und die Instanz beim naechsten Start in den Recovery-Mode schicken.
        new("Naudit:Review:Dast:Ai:Provider", false,
            AllowedValues: ["Anthropic", "Ollama", "OpenAICompatible", "ClaudeCode"]),
        new("Naudit:Review:Dast:Ai:Model", false),
        new("Naudit:Review:Dast:Ai:Endpoint", false),
        new("Naudit:Review:Dast:Ai:ApiKey", true),
        new("Naudit:Review:MaxRoundtrips", false),
        new("Naudit:Review:Memory:Enabled", false),
        new("Naudit:Review:Memory:MaxEntries", false),
        new("Naudit:Review:Resolution:Enabled", false),
        new("Naudit:Review:Resolution:LlmClassification", false),
        new("Naudit:Review:Resolution:RenderCheckbox", false),
        new("Naudit:Review:Resolution:RenderHint", false),
        new("Naudit:Review:Guidelines:Enabled", false),
        new("Naudit:Review:Guidelines:MaxSourceChars", false),
        new("Naudit:Review:Guidelines:MaxProfileChars", false),
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
