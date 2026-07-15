## Task 5: ClaudeCode CLI path (`--mcp-config` / `--allowedTools` / `--max-turns`)

**Files:**
- Modify: `src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs`
- Modify: `src/Naudit.Infrastructure/Ai/AiClientFactory.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`
- Modify: `tests/Naudit.Tests/ClaudeCodeChatClientTests.cs`

**Interfaces:**
- Consumes: `McpOptions` (Task 3).
- Produces: `AiClientFactory.Create(AiOptions, McpOptions?)`; `ClaudeCodeChatClient(AiOptions, IProcessRunner, McpOptions?)`.

- [ ] **Step 1: Write the failing tests**

In `tests/Naudit.Tests/ClaudeCodeChatClientTests.cs`, add a small helper and three tests. The `Client` helper currently builds a client without `McpOptions`; add an overload path — the client's third ctor arg is the new `McpOptions?` (null = today's behaviour):

```csharp
    [Fact]
    public async Task GetResponseAsync_mcpDisabled_keepsTodaysArgs()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub,
            new Naudit.Infrastructure.Mcp.McpOptions { Enabled = false });

        await client.GetResponseAsync(Messages());

        var args = stub.LastSpec!.Arguments.ToList();
        Assert.Equal("1", args[args.IndexOf("--max-turns") + 1]);
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);   // Tools aus
        Assert.DoesNotContain("--mcp-config", args);
        Assert.DoesNotContain("--allowedTools", args);
    }

    [Fact]
    public async Task GetResponseAsync_mcpEnabled_addsMcpConfig_allowlist_andRaisesMaxTurns()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var mcp = new Naudit.Infrastructure.Mcp.McpOptions
        {
            Enabled = true,
            MaxIterations = 5,
            Servers =
            {
                new() { Name = "context7", Transport = "http", Url = "https://mcp.context7.com/mcp", ApiKey = "sk-1" },
            },
        };
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub, mcp);

        await client.GetResponseAsync(Messages());

        var args = stub.LastSpec!.Arguments.ToList();
        Assert.Equal("5", args[args.IndexOf("--max-turns") + 1]);         // Loop erlaubt
        Assert.Contains("--mcp-config", args);
        Assert.Contains("--allowedTools", args);
        // Allowlist enthält NUR das MCP-Tool des Servers — kein Bash/Edit/Read.
        var allow = args[args.IndexOf("--allowedTools") + 1];
        Assert.Contains("mcp__context7", allow);
        Assert.DoesNotContain("Bash", allow);
        Assert.DoesNotContain("Edit", allow);
        Assert.DoesNotContain("Read", allow);
        Assert.DoesNotContain("--tools", args);   // ersetzt durch die Allowlist
    }

    [Fact]
    public async Task GetResponseAsync_mcpEnabled_mcpConfigJson_containsServerUrl()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var mcp = new Naudit.Infrastructure.Mcp.McpOptions
        {
            Enabled = true,
            Servers = { new() { Name = "context7", Transport = "http", Url = "https://mcp.context7.com/mcp" } },
        };
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub, mcp);

        await client.GetResponseAsync(Messages());

        var args = stub.LastSpec!.Arguments.ToList();
        var json = args[args.IndexOf("--mcp-config") + 1];
        Assert.Contains("context7", json);
        Assert.Contains("https://mcp.context7.com/mcp", json);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~ClaudeCodeChatClientTests"`
Expected: BUILD FAIL — the 3-arg `ClaudeCodeChatClient` ctor does not exist.

- [ ] **Step 3: Add the MCP branch to `ClaudeCodeChatClient`**

In `src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs`, add the using:

```csharp
using Naudit.Infrastructure.Mcp;
```

Change the primary constructor to accept optional `McpOptions`:

```csharp
public sealed class ClaudeCodeChatClient(AiOptions aiOptions, IProcessRunner runner, McpOptions? mcp = null) : IChatClient
```

Replace the args-building block. Current:

```csharp
        var args = new List<string>
        {
            "-p", "--output-format", "json", "--max-turns", "1", "--tools", "", "--model", model,
        };
```

with:

```csharp
        // MCP an ⇒ Loop erlauben (--max-turns N), Server registrieren (--mcp-config) und AUSSCHLIESSLICH
        // die MCP-Tools freigeben (--allowedTools mcp__<server>) — die eingebauten Datei-/Shell-Tools
        // (Bash/Edit/Read/Write) bleiben aus. MCP aus ⇒ exakt die heutigen Args (--tools "", --max-turns 1).
        var mcpEnabled = mcp is { Enabled: true, Servers.Count: > 0 };
        var args = new List<string> { "-p", "--output-format", "json", "--model", model };
        if (mcpEnabled)
        {
            args.Add("--max-turns");
            args.Add(mcp!.MaxIterations.ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Add("--mcp-config");
            args.Add(BuildMcpConfigJson(mcp));
            args.Add("--allowedTools");
            args.Add(string.Join(" ", mcp.Servers.Select(s => $"mcp__{s.Name}")));   // nur die MCP-Server
        }
        else
        {
            args.Add("--max-turns");
            args.Add("1");
            args.Add("--tools");
            args.Add("");   // Tools aus (heutiges Verhalten)
        }
```

Add the helper method (place it near `StripJsonFences`), building the Claude Code `--mcp-config` shape (`{"mcpServers":{ "<name>": { ... } }}`):

```csharp
    // Baut das Claude-Code --mcp-config-JSON aus der geteilten McpOptions-Config.
    // http ⇒ { "type":"http", "url":..., "headers": { "Authorization":"Bearer <key>" } };
    // stdio ⇒ { "command":..., "args":[...] }.
    private static string BuildMcpConfigJson(McpOptions mcp)
    {
        var servers = new Dictionary<string, object>();
        foreach (var s in mcp.Servers)
        {
            if (string.Equals(s.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
            {
                servers[s.Name] = new Dictionary<string, object?>
                {
                    ["command"] = s.Command,
                    ["args"] = s.Arguments ?? new List<string>(),
                };
            }
            else
            {
                var entry = new Dictionary<string, object?> { ["type"] = "http", ["url"] = s.Url };
                if (!string.IsNullOrWhiteSpace(s.ApiKey))
                    entry["headers"] = new Dictionary<string, string> { ["Authorization"] = $"Bearer {s.ApiKey}" };
                servers[s.Name] = entry;
            }
        }
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["mcpServers"] = servers });
    }
```

- [ ] **Step 4: Thread `McpOptions` through `AiClientFactory`**

In `src/Naudit.Infrastructure/Ai/AiClientFactory.cs`, add the using:

```csharp
using Naudit.Infrastructure.Mcp;
```

Change the signature and the ClaudeCode case:

```csharp
    public static IChatClient Create(AiOptions options, McpOptions? mcp = null)
```

```csharp
            case AiProvider.ClaudeCode:
                // Kein RequireApiKey: die CLI authentifiziert über die Umgebung (Abo statt Key).
                // MCP-Config wird an den CLI-Client durchgereicht (CLI-natives MCP, nicht ChatOptions.Tools).
                return new ClaudeCodeChatClient(options, new SystemProcessRunner(), mcp);
```

- [ ] **Step 5: Pass `mcpOptions` at the DI call site**

In `src/Naudit.Infrastructure/DependencyInjection.cs`, `mcpOptions` is already bound just above this registration (Task 3, Step 4). Change the `IChatClient` registration from:

```csharp
        services.AddSingleton<IChatClient>(_ => AiClientFactory.Create(aiOptions));
```

to:

```csharp
        services.AddSingleton<IChatClient>(_ => AiClientFactory.Create(aiOptions, mcpOptions));
```

(Task 6 replaces this lambda again to add the function-invocation wrapper.)

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~ClaudeCodeChatClientTests"`
Expected: PASS (existing arg tests still green because MCP defaults to off; three new tests pass).

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs src/Naudit.Infrastructure/Ai/AiClientFactory.cs \
        src/Naudit.Infrastructure/DependencyInjection.cs tests/Naudit.Tests/ClaudeCodeChatClientTests.cs
git commit -m "feat(mcp): ClaudeCode-CLI-Pfad — --mcp-config + MCP-only-Allowlist + --max-turns"
```

---

