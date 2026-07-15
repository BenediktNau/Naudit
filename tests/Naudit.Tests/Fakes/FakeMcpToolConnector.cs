using Microsoft.Extensions.AI;
using Naudit.Infrastructure.Mcp;

namespace Naudit.Tests.Fakes;

// Pro Server-Name: entweder eine Tool-Liste oder eine Exception (für Fail-open-Tests). Zählt Aufrufe.
internal sealed class FakeMcpToolConnector : IMcpToolConnector
{
    private readonly Dictionary<string, Func<IReadOnlyList<AITool>>> _byServer = new();
    public int CallCount { get; private set; }

    public FakeMcpToolConnector Returns(string server, params AITool[] tools)
    {
        _byServer[server] = () => tools;
        return this;
    }

    public FakeMcpToolConnector Throws(string server)
    {
        _byServer[server] = () => throw new InvalidOperationException($"boom:{server}");
        return this;
    }

    public Task<IReadOnlyList<AITool>> ConnectAndListAsync(McpServerConfig server, CancellationToken ct = default)
    {
        CallCount++;
        if (_byServer.TryGetValue(server.Name, out var f))
            return Task.FromResult(f());
        return Task.FromResult<IReadOnlyList<AITool>>([]);
    }
}
