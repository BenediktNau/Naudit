## Task 3: `McpOptions` config binding + SettingsCatalog entries

**Files:**
- Create: `src/Naudit.Infrastructure/Mcp/McpOptions.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`
- Modify: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`
- Modify: `tests/Naudit.Tests/` — add `McpOptionsTests.cs`

**Interfaces:**
- Produces: `McpOptions { bool Enabled; int MaxIterations; List<McpServerConfig> Servers }`; `McpServerConfig { string Name; string Transport; string? Url; string? Command; List<string>? Arguments; string? ApiKey }`. Registered as a singleton `McpOptions` in DI.

- [ ] **Step 1: Write the failing test**

Create `tests/Naudit.Tests/McpOptionsTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Mcp;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

public class McpOptionsTests
{
    [Fact]
    public void Binds_enabled_iterations_and_serverList()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Naudit:Review:Mcp:Enabled"] = "true",
            ["Naudit:Review:Mcp:MaxIterations"] = "6",
            ["Naudit:Review:Mcp:Servers:0:Name"] = "context7",
            ["Naudit:Review:Mcp:Servers:0:Transport"] = "http",
            ["Naudit:Review:Mcp:Servers:0:Url"] = "https://mcp.context7.com/mcp",
            ["Naudit:Review:Mcp:Servers:0:ApiKey"] = "sk-123",
        }).Build();

        var opts = config.GetSection("Naudit:Review:Mcp").Get<McpOptions>()!;

        Assert.True(opts.Enabled);
        Assert.Equal(6, opts.MaxIterations);
        var server = Assert.Single(opts.Servers);
        Assert.Equal("context7", server.Name);
        Assert.Equal("https://mcp.context7.com/mcp", server.Url);
        Assert.Equal("sk-123", server.ApiKey);
    }

    [Fact]
    public void Catalog_hasEnabledAndMaxIterationsScalars()
    {
        Assert.True(SettingsCatalog.TryGet("Naudit:Review:Mcp:Enabled", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Review:Mcp:MaxIterations", out _));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~McpOptionsTests"`
Expected: BUILD FAIL — `McpOptions` does not exist.

- [ ] **Step 3: Create `McpOptions`**

Create `src/Naudit.Infrastructure/Mcp/McpOptions.cs`:

```csharp
namespace Naudit.Infrastructure.Mcp;

/// <summary>Naudit:Review:Mcp — MCP-Tools in der Review-Runtime. Enabled=false ⇒ heutiger Single-Shot.</summary>
public sealed class McpOptions
{
    /// <summary>Master-Schalter. Aus ⇒ keine Tools, kein Function-Invocation-Loop.</summary>
    public bool Enabled { get; set; }

    /// <summary>Obergrenze der Tool-Runden pro Review (Token-/Latenz-Schutz). Beide Provider-Pfade.</summary>
    public int MaxIterations { get; set; } = 4;

    /// <summary>Konfigurierte MCP-Server (Liste ⇒ env-/appsettings-geformt, wie ProjectTokens).</summary>
    public List<McpServerConfig> Servers { get; set; } = new();
}

/// <summary>Ein MCP-Server. Transport "http" (Url) oder "stdio" (Command/Arguments).
/// ApiKey (Secret) wird bei http als Authorization-Bearer-Header gesetzt.</summary>
public sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public string Transport { get; set; } = "http";
    public string? Url { get; set; }
    public string? Command { get; set; }
    public List<string>? Arguments { get; set; }
    public string? ApiKey { get; set; }
}
```

- [ ] **Step 4: Bind it in DI**

In `src/Naudit.Infrastructure/DependencyInjection.cs`, bind `mcpOptions` **immediately after** `var aiOptions = configuration.GetSection("Naudit:Ai").Get<AiOptions>() ?? new AiOptions();` and **before** the `services.AddSingleton<IChatClient>(...)` line — so Tasks 5–6 (which change that registration) can use it:

```csharp
        // MCP-Runtime-Config (Naudit:Review:Mcp). Vor der IChatClient-Registrierung binden, damit der
        // Client-Wrap + der ClaudeCode-CLI-Pfad sie teilen. Singleton für die Review-Pipeline.
        var mcpOptions = configuration.GetSection("Naudit:Review:Mcp").Get<McpOptions>() ?? new McpOptions();
        services.AddSingleton(mcpOptions);
```

Add the using at the top of the file if not present:

```csharp
using Naudit.Infrastructure.Mcp;
```

- [ ] **Step 5: Add the two scalar catalog entries**

In `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`, add to the `All` list after the `Naudit:Review:Gate:MinConfidence` entry:

```csharp
        new("Naudit:Review:Mcp:Enabled", false),
        new("Naudit:Review:Mcp:MaxIterations", false),
```

(The per-server `Servers:*` keys — including `ApiKey` — stay env-only, following the `ProjectTokens`/`Ui:Admins` list-shaped precedent noted in the catalog's own comment.)

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~McpOptionsTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Infrastructure/Mcp/McpOptions.cs src/Naudit.Infrastructure/DependencyInjection.cs \
        src/Naudit.Infrastructure/Settings/SettingsCatalog.cs tests/Naudit.Tests/McpOptionsTests.cs
git commit -m "feat(mcp): McpOptions binden (Naudit:Review:Mcp) + Settings-Katalog-Scalars"
```

---

