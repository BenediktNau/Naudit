using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Naudit.Infrastructure.Mcp;

/// <summary>Echte MCP-Anbindung über das ModelContextProtocol-SDK. NUR manuell E2E getestet
/// (wie der reale Git-/LLM-Pfad) — die Orchestrierung/Fail-open deckt McpReviewToolProviderTests ab.
/// Der McpClient bleibt über die zurückgegebenen Tool-Referenzen am Leben (Tool-Aufruf ruft über ihn),
/// die Verbindung lebt bewusst für die Prozesslaufzeit (Singleton-Provider cached die Tools).</summary>
public sealed class McpClientToolConnector(ILoggerFactory loggerFactory) : IMcpToolConnector
{
    public async Task<IReadOnlyList<AITool>> ConnectAndListAsync(McpServerConfig server, CancellationToken ct = default)
    {
        var transport = BuildTransport(server);
        var client = await McpClient.CreateAsync(transport, null, loggerFactory, ct);
        // Overload-Disambiguierung: getypter null-Options-Wert wählt die auto-paginierende
        // Überladung, die IList<McpClientTool> liefert (SDK-Version-sensibel; die
        // ListToolsRequestParams-Überladung gäbe stattdessen das rohe ListToolsResult zurück).
        var tools = await client.ListToolsAsync((RequestOptions?)null, ct);
        return [.. tools];   // McpClientTool : AIFunction : AITool
    }

    // http ⇒ HttpClientTransport (Streamable-HTTP/SSE auto), ApiKey als Authorization-Bearer-Header.
    // stdio ⇒ StdioClientTransport (lokaler Prozess).
    private IClientTransport BuildTransport(McpServerConfig server)
    {
        if (string.Equals(server.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command ?? throw new InvalidOperationException($"MCP-Server {server.Name}: Command fehlt (stdio)."),
                Arguments = server.Arguments,
            });
        }

        var options = new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Url ?? throw new InvalidOperationException($"MCP-Server {server.Name}: Url fehlt (http).")),
            TransportMode = HttpTransportMode.AutoDetect,
        };
        if (!string.IsNullOrWhiteSpace(server.ApiKey))
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {server.ApiKey}" };
        return new HttpClientTransport(options, loggerFactory);
    }
}
