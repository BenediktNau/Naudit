using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Naudit.Infrastructure.Mcp;

/// <summary>Echte MCP-Anbindung über das ModelContextProtocol-SDK. NUR manuell E2E getestet
/// (wie der reale Git-/LLM-Pfad) — die Orchestrierung/Fail-open deckt McpReviewToolProviderTests ab.
/// Der McpClient bleibt über die zurückgegebenen Tool-Referenzen am Leben (Tool-Aufruf ruft über ihn),
/// die Verbindung lebt bewusst für die Prozesslaufzeit (Singleton-Provider cached die Tools). Damit die
/// Prozesse/Verbindungen trotzdem sauber beim App-Shutdown enden (statt bis zum harten Kill offen zu
/// bleiben), hält dieser Connector selbst jeden erfolgreich verbundenen McpClient und implementiert
/// IAsyncDisposable — der Connector ist als Singleton registriert, der Container ruft DisposeAsync
/// beim Herunterfahren des Hosts (WebApplication.DisposeAsync) automatisch auf.</summary>
public sealed class McpClientToolConnector(ILoggerFactory loggerFactory) : IMcpToolConnector, IAsyncDisposable
{
    // Nur ERFOLGREICH verbundene Clients werden gehalten (die Tools rufen über sie) — ein Client, dessen
    // ListToolsAsync fehlschlägt, wird sofort wieder geschlossen (s. u.), sonst würde bei jedem erneuten
    // Versuch eines dauerhaft nicht erreichbaren Servers (McpReviewToolProvider ruft pro Review erneut an,
    // solange kein Server erfolgreich war) ein weiterer, nie geschlossener Client anfallen.
    private readonly List<McpClient> _clients = new();
    private readonly object _clientsLock = new();
    private readonly ILogger _logger = loggerFactory.CreateLogger<McpClientToolConnector>();

    public async Task<IReadOnlyList<AITool>> ConnectAndListAsync(McpServerConfig server, CancellationToken ct = default)
    {
        var transport = BuildTransport(server);
        var client = await McpClient.CreateAsync(transport, null, loggerFactory, ct);
        try
        {
            // Overload-Disambiguierung: getypter null-Options-Wert wählt die auto-paginierende
            // Überladung, die IList<McpClientTool> liefert (SDK-Version-sensibel; die
            // ListToolsRequestParams-Überladung gäbe stattdessen das rohe ListToolsResult zurück).
            var tools = await client.ListToolsAsync((RequestOptions?)null, ct);
            lock (_clientsLock)
                _clients.Add(client);   // ab jetzt hält der Connector die Verbindung bis zum Shutdown
            return [.. tools];   // McpClientTool : AIFunction : AITool
        }
        catch
        {
            await client.DisposeAsync();   // wird nicht gecacht/gehalten — sofort wieder schließen
            throw;
        }
    }

    /// <summary>Schließt alle bislang verbundenen MCP-Clients (Prozesse/HTTP-Verbindungen) beim
    /// App-Shutdown. Best-effort: ein einzelner Fehler beim Schließen bricht die übrigen nicht ab.</summary>
    public async ValueTask DisposeAsync()
    {
        List<McpClient> toClose;
        lock (_clientsLock)
        {
            toClose = new List<McpClient>(_clients);
            _clients.Clear();
        }
        foreach (var client in toClose)
        {
            try { await client.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "MCP-Client konnte beim Shutdown nicht sauber geschlossen werden."); }
        }
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
