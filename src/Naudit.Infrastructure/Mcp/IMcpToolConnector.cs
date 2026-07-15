using Microsoft.Extensions.AI;

namespace Naudit.Infrastructure.Mcp;

/// <summary>Verbindet EINEN MCP-Server und listet dessen Tools als MEAI-AITool. Seam, damit
/// McpReviewToolProvider ohne echten Server getestet wird (echte Impl: McpClientToolConnector).</summary>
public interface IMcpToolConnector
{
    Task<IReadOnlyList<AITool>> ConnectAndListAsync(McpServerConfig server, CancellationToken ct = default);
}
