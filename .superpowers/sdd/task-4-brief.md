### Task 4: `DastProbeSession` — the MCP client over the exec stream

**Files:**
- Create: `src/Naudit.Infrastructure/Dast/DastProbeSession.cs`
- Test: `tests/Naudit.Tests/DastProbeSessionTests.cs`

**Interfaces:**
- Consumes: `IDockerClient.ExecStreamAsync` (Task 3), `DastOptions.ProbeMcpArgv` (Task 2), `RunningApp.ProbeContainerName` (PR 1), `ModelContextProtocol.Protocol.StreamClientTransport`, `ModelContextProtocol.Client.McpClient`.
- Produces: `DastProbeSession.StartAsync(IDockerClient docker, DastOptions options, string probeContainer, ILoggerFactory loggerFactory, CancellationToken ct) -> Task<DastProbeSession>`; instance exposes `IReadOnlyList<AITool> Tools`; `IAsyncDisposable` disposes the `McpClient` then the exec stream.

- [ ] **Step 1: Write the failing test**

Because `McpClient.CreateAsync` speaks the real MCP protocol over the stream, a pure unit test cannot boot a real server. Test what is deterministic without a live MCP peer: that the session launches the correct exec argv in the probe container and that a start failure is surfaced as a thrown `DockerUnavailableException`-derived/`InvalidOperationException` for the analyzer to catch (fail-open lives in the analyzer, Task 5/6 — the session may throw). Create `tests/Naudit.Tests/DastProbeSessionTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Dast;
using Naudit.Infrastructure.Docker;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class DastProbeSessionTests
{
    [Fact]
    public async Task Start_execsProbeArgv_inProbeContainer()
    {
        var docker = new ThrowAfterExecDocker();   // lässt ExecStream zu, MCP-Handshake schlägt dann fehl
        var options = new DastOptions();

        await Assert.ThrowsAnyAsync<Exception>(() => DastProbeSession.StartAsync(
            docker, options, "naudit-dast-pw-xyz", NullLoggerFactory.Instance, CancellationToken.None));

        var call = Assert.Single(docker.ExecStreamCalls);
        Assert.Equal("naudit-dast-pw-xyz", call.Container);
        Assert.Equal(options.ProbeMcpArgv, call.Argv);
    }

    /// <summary>ExecStream liefert einen Stream, auf dem der MCP-Handshake nie antwortet ⇒ StartAsync
    /// muss (mit Timeout/Fehler) werfen statt zu hängen; der Analyzer fängt das fail-open.</summary>
    private sealed class ThrowAfterExecDocker : FakeDockerClient
    {
        // NextExecStdout bleibt leer ⇒ McpClient.CreateAsync bekommt EOF/kein Handshake ⇒ Fehler.
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DastProbeSessionTests`
Expected: FAIL — `DastProbeSession` does not exist (CS0246).

- [ ] **Step 3: Write the session**

Create `src/Naudit.Infrastructure/Dast/DastProbeSession.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Naudit.Infrastructure.Docker;

namespace Naudit.Infrastructure.Dast;

/// <summary>Eine MCP-Sitzung je Review: startet den Playwright-MCP-Server als stdio-Prozess im
/// Probe-Container (docker exec, attached duplex), verbindet einen McpClient über die Stream-Naht und
/// listet die Browser-Tools. Kurzlebig — DisposeAsync schließt Client UND exec-Stream. Anders als der
/// prozesslebenslange McpReviewToolProvider (Review-Tool-Loop) gehört diese Sitzung genau einem Lauf.</summary>
public sealed class DastProbeSession : IAsyncDisposable
{
    private readonly McpClient _client;
    private readonly DockerExecStream _exec;

    public IReadOnlyList<AITool> Tools { get; }

    private DastProbeSession(McpClient client, DockerExecStream exec, IReadOnlyList<AITool> tools)
    {
        _client = client; _exec = exec; Tools = tools;
    }

    public static async Task<DastProbeSession> StartAsync(IDockerClient docker, DastOptions options,
        string probeContainer, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var exec = await docker.ExecStreamAsync(probeContainer, options.ProbeMcpArgv,
            environment: null, workingDirectory: "/", ct);
        try
        {
            // serverInput = was WIR schreiben (Server-stdin), serverOutput = was wir lesen (Server-stdout).
            var transport = new StreamClientTransport(serverInput: exec.Stdin, serverOutput: exec.Stdout, loggerFactory);
            var client = await McpClient.CreateAsync(transport, null, loggerFactory, ct);
            var tools = await client.ListToolsAsync((RequestOptions?)null, ct);
            return new DastProbeSession(client, exec, [.. tools]);
        }
        catch
        {
            await exec.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _client.DisposeAsync(); }
        finally { await _exec.DisposeAsync(); }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DastProbeSessionTests`
Expected: PASS — the argv is recorded before the handshake fails; `StartAsync` throws (no MCP peer answers), which the test asserts. If `McpClient.CreateAsync` blocks instead of failing on an unresponsive stream, wrap the handshake in a short linked-timeout CTS inside `StartAsync` (e.g. 30s) so it fails deterministically; add a German comment explaining the timeout. Re-run.

- [ ] **Step 5: Full suite**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS — 704.

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Dast/DastProbeSession.cs tests/Naudit.Tests/DastProbeSessionTests.cs
git commit -m "feat(dast): DastProbeSession — Playwright-MCP über exec-stdio, kurzlebig je Review"
```

---

