using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Mcp;
using Xunit;

namespace Naudit.Tests;

// McpClientToolConnector spricht echte MCP-Server (Prozess/HTTP) an — ein vollständiger Verbindungs-
// /Dispose-Test braucht daher einen echten Server (s. Klassenkommentar in McpClientToolConnector: nur
// manuell E2E getestet). Hier wird ausschließlich die Shutdown-Naht selbst geprüft: der Connector ist
// als Singleton registriert (DependencyInjection.cs), der DI-Container ruft DisposeAsync beim
// App-Shutdown auf — dafür muss der Typ IAsyncDisposable implementieren und ohne je verbundene Clients
// (der Normalfall in den meisten Testläufen) klaglos disposen.
public class McpClientToolConnectorTests
{
    [Fact]
    public void ImplementsIAsyncDisposable_soTheDiContainerCanCloseClientsOnShutdown()
    {
        // Nur der Connector hält die McpClient-Referenzen (die zurückgegebenen Tools rufen über sie) —
        // die Naht für "Finding 3" (McpClient nie disposed) ist also, dass genau dieser Typ
        // IAsyncDisposable implementiert und als Singleton registriert ist.
        Assert.IsAssignableFrom<IAsyncDisposable>(new McpClientToolConnector(NullLoggerFactory.Instance));
    }

    [Fact]
    public async Task DisposeAsync_withNoConnectedClients_completesWithoutThrowing()
    {
        var connector = new McpClientToolConnector(NullLoggerFactory.Instance);

        await connector.DisposeAsync();
    }
}
