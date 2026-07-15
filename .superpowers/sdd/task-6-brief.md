## Task 6: Real MCP connector + package + DI wiring (function-invocation)

**Files:**
- Modify: `src/Naudit.Infrastructure/Naudit.Infrastructure.csproj`
- Create: `src/Naudit.Infrastructure/Mcp/McpClientToolConnector.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`
- Create: `tests/Naudit.Tests/McpDiCompositionTests.cs`

**Interfaces:**
- Consumes: `IMcpToolConnector`, `McpReviewToolProvider` (Task 4); `McpOptions` (Task 3); `IReviewToolProvider`/`NullReviewToolProvider` (Task 1).
- Produces: `McpClientToolConnector : IMcpToolConnector`; conditional DI registration.

- [ ] **Step 1: Add the `ModelContextProtocol.Core` package**

In `src/Naudit.Infrastructure/Naudit.Infrastructure.csproj`, add inside the `<ItemGroup>` with the other `PackageReference`s:

```xml
    <PackageReference Include="ModelContextProtocol.Core" Version="1.4.1" />
```

Run: `dotnet restore Naudit.slnx`
Expected: restore succeeds, `ModelContextProtocol.Core 1.4.1` resolved.

- [ ] **Step 2: Implement the real connector**

Create `src/Naudit.Infrastructure/Mcp/McpClientToolConnector.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

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
        // Overload-Disambiguierung: getypter null-Params-Wert (SDK-Version-sensibel).
        var tools = await client.ListToolsAsync((ListToolsRequestParams?)null, ct);
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
```

> **Note for the implementer:** the `McpClient.CreateAsync` / `ListToolsAsync` overloads and `HttpClientTransportOptions` property names are pinned to `ModelContextProtocol.Core 1.4.1` (verified against its XML docs). If a build error appears here after a package bump, it is a version-name drift, not a logic bug — reconcile against the new package's `ModelContextProtocol.Core.xml`. The exact Context7 auth header may differ from `Authorization: Bearer`; confirm during manual E2E (Step 6) and adjust `AdditionalHeaders`.

- [ ] **Step 3: Wire the conditional DI registration**

In `src/Naudit.Infrastructure/DependencyInjection.cs`:

First, wrap the MEAI client with the function-invocation loop. Change the `IChatClient` registration (from Task 5) to:

```csharp
        // Global-Client. Bei aktivem MCP + MEAI-Provider mit Function-Invocation-Loop umhüllen
        // (Cap = MaxIterations). ClaudeCode ist ein eigener IChatClient (CLI-natives MCP) und wird NICHT umhüllt.
        services.AddSingleton<IChatClient>(sp =>
        {
            var client = AiClientFactory.Create(aiOptions, mcpOptions);
            if (mcpOptions.Enabled && aiOptions.Provider != AiProvider.ClaudeCode)
                client = client.AsBuilder()
                    .UseFunctionInvocation(sp.GetService<ILoggerFactory>(),
                        c => c.MaximumIterationsPerRequest = mcpOptions.MaxIterations)
                    .Build();
            return client;
        });
```

Then, replace the default `NullReviewToolProvider` registration (added in Task 1) with the conditional one:

```csharp
        // MCP-Tools: MEAI-Provider + MCP an ⇒ echte MCP-Tools (Function-Invocation nutzt ChatOptions.Tools);
        // sonst No-Op (MCP aus, oder ClaudeCode ⇒ CLI-natives MCP über --mcp-config).
        if (mcpOptions.Enabled && aiOptions.Provider != AiProvider.ClaudeCode)
        {
            services.AddSingleton<IMcpToolConnector>(sp => new McpClientToolConnector(sp.GetRequiredService<ILoggerFactory>()));
            services.AddSingleton<IReviewToolProvider>(sp => new McpReviewToolProvider(
                mcpOptions,
                sp.GetRequiredService<IMcpToolConnector>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<McpReviewToolProvider>()));
        }
        else
        {
            services.AddSingleton<IReviewToolProvider>(new NullReviewToolProvider());
        }
```

Ensure `using Microsoft.Extensions.AI;` is present (for `AsBuilder`/`UseFunctionInvocation`) — it is used elsewhere; add if the build complains.

- [ ] **Step 4: Write the DI composition test**

Create `tests/Naudit.Tests/McpDiCompositionTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Mcp;
using Xunit;

namespace Naudit.Tests;

public class McpDiCompositionTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNauditInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void McpDisabled_resolvesNullToolProvider()
    {
        using var sp = Build(new Dictionary<string, string?>
        {
            ["Naudit:Ai:Provider"] = "OpenAICompatible",
            ["Naudit:Ai:ApiKey"] = "k",
            ["Naudit:Review:Mcp:Enabled"] = "false",
        });

        Assert.IsType<NullReviewToolProvider>(sp.GetRequiredService<IReviewToolProvider>());
    }

    [Fact]
    public void McpEnabled_meaiProvider_resolvesMcpToolProvider()
    {
        using var sp = Build(new Dictionary<string, string?>
        {
            ["Naudit:Ai:Provider"] = "OpenAICompatible",
            ["Naudit:Ai:ApiKey"] = "k",
            ["Naudit:Review:Mcp:Enabled"] = "true",
            ["Naudit:Review:Mcp:Servers:0:Name"] = "context7",
            ["Naudit:Review:Mcp:Servers:0:Url"] = "https://mcp.context7.com/mcp",
        });

        Assert.IsType<McpReviewToolProvider>(sp.GetRequiredService<IReviewToolProvider>());
    }

    [Fact]
    public void McpEnabled_claudeCodeProvider_stillResolvesNullToolProvider()
    {
        using var sp = Build(new Dictionary<string, string?>
        {
            ["Naudit:Ai:Provider"] = "ClaudeCode",
            ["Naudit:Review:Mcp:Enabled"] = "true",
            ["Naudit:Review:Mcp:Servers:0:Name"] = "context7",
            ["Naudit:Review:Mcp:Servers:0:Url"] = "https://mcp.context7.com/mcp",
        });

        // ClaudeCode nutzt CLI-natives MCP ⇒ kein ChatOptions.Tools-Provider.
        Assert.IsType<NullReviewToolProvider>(sp.GetRequiredService<IReviewToolProvider>());
    }
}
```

> **Note:** `AddNauditInfrastructure` reads more than the AI section (git platform, DB, etc.). If building the full provider requires additional config keys or a DB, mirror the setup used by the existing infrastructure/DI tests in this project (check `TestAppFactory.cs` / any existing `*DiTests`/`DependencyInjection` test for the minimal key set and DB bootstrap) and reuse that pattern here. If a full `BuildServiceProvider()` is impractical in a unit test, assert the registration instead by inspecting the `IServiceCollection` (find the `ServiceDescriptor` for `IReviewToolProvider` and assert its `ImplementationType`/instance) without building the provider.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test Naudit.slnx`
Expected: PASS (whole suite, including the new DI composition tests).

- [ ] **Step 6: Manual E2E (real Context7)**

This is the real-path verification (CI never hits a live MCP server, mirroring the project's manual end-to-end convention).

1. Set config (user-secrets on the Web project), e.g. for an OpenAI-compatible provider:
   ```bash
   dotnet user-secrets set "Naudit:Review:Mcp:Enabled" "true" --project src/Naudit.Web
   dotnet user-secrets set "Naudit:Review:Mcp:Servers:0:Name" "context7" --project src/Naudit.Web
   dotnet user-secrets set "Naudit:Review:Mcp:Servers:0:Transport" "http" --project src/Naudit.Web
   dotnet user-secrets set "Naudit:Review:Mcp:Servers:0:Url" "https://mcp.context7.com/mcp" --project src/Naudit.Web
   # optional: dotnet user-secrets set "Naudit:Review:Mcp:Servers:0:ApiKey" "<context7-key>" --project src/Naudit.Web
   ```
2. `dotnet run --project src/Naudit.Web`, trigger a review (webhook or `POST /review`) on a diff that uses a well-known library API.
3. Confirm in the logs that the tool was offered and (for a relevant diff) called, and that a valid review JSON still came back. If the Context7 server rejects the auth header, adjust `AdditionalHeaders` / the `--mcp-config` `headers` shape per Context7's docs.
4. Repeat with `Naudit:Ai:Provider=ClaudeCode` to exercise the CLI `--mcp-config` path (requires the `claude` CLI logged in).

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Infrastructure/Naudit.Infrastructure.csproj src/Naudit.Infrastructure/Mcp/McpClientToolConnector.cs \
        src/Naudit.Infrastructure/DependencyInjection.cs tests/Naudit.Tests/McpDiCompositionTests.cs
git commit -m "feat(mcp): echter McpClientToolConnector + DI-Verdrahtung (Function-Invocation-Loop)"
```

---

