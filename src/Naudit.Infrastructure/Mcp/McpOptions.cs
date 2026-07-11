namespace Naudit.Infrastructure.Mcp;

/// <summary>Naudit:Review:Mcp — MCP-Tools in der Review-Runtime. Enabled=false ⇒ heutiger Single-Shot.</summary>
public sealed class McpOptions
{
    /// <summary>Master-Schalter. Aus ⇒ keine Tools, kein Function-Invocation-Loop.</summary>
    public bool Enabled { get; set; }

    /// <summary>Obergrenze der Tool-Runden pro Review (Token-/Latenz-Schutz). Beide Provider-Pfade.</summary>
    public int MaxIterations { get; set; } = 4;

    /// <summary>Konfigurierte MCP-Server (Liste ⇒ env-/appsettings-geformt, wie ProjectTokens).</summary>
    public List<McpServerConfig> Servers { get; set; } = new();
}

/// <summary>Ein MCP-Server. Transport "http" (Url) oder "stdio" (Command/Arguments).
/// ApiKey (Secret) wird bei http als Authorization-Bearer-Header gesetzt.</summary>
public sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public string Transport { get; set; } = "http";
    public string? Url { get; set; }
    public string? Command { get; set; }
    public List<string>? Arguments { get; set; }
    public string? ApiKey { get; set; }
}
