## Task 4: `McpReviewToolProvider` orchestration (fail-open + cache)

**Files:**
- Create: `src/Naudit.Infrastructure/Mcp/IMcpToolConnector.cs`
- Create: `src/Naudit.Infrastructure/Mcp/McpReviewToolProvider.cs`
- Create: `tests/Naudit.Tests/Fakes/FakeMcpToolConnector.cs`
- Create: `tests/Naudit.Tests/McpReviewToolProviderTests.cs`

**Interfaces:**
- Consumes: `McpOptions`, `McpServerConfig` (Task 3); `IReviewToolProvider` (Task 1).
- Produces: `IMcpToolConnector.ConnectAndListAsync(McpServerConfig, CancellationToken) → Task<IReadOnlyList<AITool>>`; `McpReviewToolProvider(McpOptions, IMcpToolConnector, ILogger<McpReviewToolProvider>)`.

- [ ] **Step 1: Add the connector seam and the fake**

Create `src/Naudit.Infrastructure/Mcp/IMcpToolConnector.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Naudit.Infrastructure.Mcp;

/// <summary>Verbindet EINEN MCP-Server und listet dessen Tools als MEAI-AITool. Seam, damit
/// McpReviewToolProvider ohne echten Server getestet wird (echte Impl: McpClientToolConnector).</summary>
public interface IMcpToolConnector
{
    Task<IReadOnlyList<AITool>> ConnectAndListAsync(McpServerConfig server, CancellationToken ct = default);
}
```

Create `tests/Naudit.Tests/Fakes/FakeMcpToolConnector.cs`:

```csharp
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
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Naudit.Tests/McpReviewToolProviderTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Mcp;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class McpReviewToolProviderTests
{
    private static readonly ReviewRequest Request = new("1", 1, "T");

    private static McpServerConfig Server(string name) => new() { Name = name, Transport = "http", Url = "http://x" };
    private static AITool Tool(string name) => AIFunctionFactory.Create(() => "r", name);

    private static McpReviewToolProvider Provider(McpOptions opts, IMcpToolConnector connector)
        => new(opts, connector, NullLogger<McpReviewToolProvider>.Instance);

    [Fact]
    public async Task Disabled_returnsEmpty_andNeverCallsConnector()
    {
        var connector = new FakeMcpToolConnector().Returns("a", Tool("t"));
        var opts = new McpOptions { Enabled = false, Servers = { Server("a") } };

        var tools = await Provider(opts, connector).GetToolsAsync(Request);

        Assert.Empty(tools);
        Assert.Equal(0, connector.CallCount);
    }

    [Fact]
    public async Task Aggregates_toolsFromAllServers()
    {
        var connector = new FakeMcpToolConnector().Returns("a", Tool("t1")).Returns("b", Tool("t2"));
        var opts = new McpOptions { Enabled = true, Servers = { Server("a"), Server("b") } };

        var tools = await Provider(opts, connector).GetToolsAsync(Request);

        Assert.Equal(2, tools.Count);
    }

    [Fact]
    public async Task FailingServer_isSkipped_othersStillReturn()
    {
        var connector = new FakeMcpToolConnector().Throws("a").Returns("b", Tool("t2"));
        var opts = new McpOptions { Enabled = true, Servers = { Server("a"), Server("b") } };

        var tools = await Provider(opts, connector).GetToolsAsync(Request);

        var tool = Assert.Single(tools);
        Assert.Equal("t2", tool.Name);
    }

    [Fact]
    public async Task Caches_nonEmptyResult_acrossCalls()
    {
        var connector = new FakeMcpToolConnector().Returns("a", Tool("t1"));
        var opts = new McpOptions { Enabled = true, Servers = { Server("a") } };
        var provider = Provider(opts, connector);

        await provider.GetToolsAsync(Request);
        await provider.GetToolsAsync(Request);

        Assert.Equal(1, connector.CallCount);   // zweiter Review nutzt den Cache
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~McpReviewToolProviderTests"`
Expected: BUILD FAIL — `McpReviewToolProvider` does not exist.

- [ ] **Step 4: Implement the provider**

Create `src/Naudit.Infrastructure/Mcp/McpReviewToolProvider.cs`:

```csharp
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
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~McpReviewToolProviderTests"`
Expected: PASS (all four).

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Mcp/IMcpToolConnector.cs src/Naudit.Infrastructure/Mcp/McpReviewToolProvider.cs \
        tests/Naudit.Tests/Fakes/FakeMcpToolConnector.cs tests/Naudit.Tests/McpReviewToolProviderTests.cs
git commit -m "feat(mcp): McpReviewToolProvider — Server aggregieren, fail-open, cachen"
```

---

