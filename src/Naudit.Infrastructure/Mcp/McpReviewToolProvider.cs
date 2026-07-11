using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Mcp;

/// <summary>Baut die MEAI-Tools aus den konfigurierten MCP-Servern. Fail-open: ein nicht
/// erreichbarer Server ⇒ dieser Server fällt weg, der Review läuft (tool-los) weiter — wie ein
/// fehlgeschlagener SAST-Checkout → diff-only. Erfolgreiche Tool-Liste wird für die Prozesslaufzeit
/// gecacht (Server-Host fix, Katalog stabil); ein leeres Ergebnis wird NICHT gecacht (nächster
/// Review versucht erneut, damit ein zwischenzeitlich erreichbarer Server aufgenommen wird).</summary>
public sealed class McpReviewToolProvider(
    McpOptions options, IMcpToolConnector connector, ILogger<McpReviewToolProvider> logger) : IReviewToolProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<AITool>? _cached;

    public async Task<IReadOnlyList<AITool>> GetToolsAsync(ReviewRequest request, CancellationToken ct = default)
    {
        if (!options.Enabled || options.Servers.Count == 0)
            return [];

        if (_cached is { Count: > 0 })
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { Count: > 0 })
                return _cached;

            var tools = new List<AITool>();
            foreach (var server in options.Servers)
            {
                try
                {
                    tools.AddRange(await connector.ConnectAndListAsync(server, ct));
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "MCP-Server {Server} nicht erreichbar — Review läuft ohne dessen Tools.", server.Name);
                }
            }

            if (tools.Count > 0)
                _cached = tools;   // nur Erfolg cachen
            return tools;
        }
        finally
        {
            _gate.Release();
        }
    }
}
