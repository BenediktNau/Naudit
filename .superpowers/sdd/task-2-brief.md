### Task 2: `DastOptions` — probing knobs

**Files:**
- Modify: `src/Naudit.Infrastructure/Dast/DastOptions.cs`
- Test: `tests/Naudit.Tests/DastOptionsTests.cs`

**Interfaces:**
- Produces: `DastOptions.MaxProbeSteps` (int, default 12); `DastOptions.ProbeMcpArgv` (`IReadOnlyList<string>`, the exec argv launching the stdio MCP server).

- [ ] **Step 1: Write the failing test**

Add to `tests/Naudit.Tests/DastOptionsTests.cs`:

```csharp
    [Fact]
    public void Defaults_probingKnobs()
    {
        var options = new DastOptions();

        Assert.Equal(12, options.MaxProbeSteps);
        Assert.Equal(
            new[] { "node", "/app/cli.js", "--headless", "--browser", "chromium", "--no-sandbox" },
            options.ProbeMcpArgv);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~Defaults_probingKnobs"`
Expected: FAIL — `MaxProbeSteps`/`ProbeMcpArgv` do not exist (CS1061).

- [ ] **Step 3: Add the members**

In `src/Naudit.Infrastructure/Dast/DastOptions.cs`, next to the existing probing-related fields:

```csharp
    /// <summary>Deckel für den agentischen Probing-Loop (Tool-Aufrufe + Modell-Turns zusammen).
    /// Token-frugal: die dynamische Prüfung ist Grounding, kein erschöpfender Scan.</summary>
    public int MaxProbeSteps { get; set; } = 12;

    /// <summary>Kommando, das den Playwright-MCP-Server als stdio-Prozess im Probe-Container startet
    /// (docker exec). Kein --port ⇒ stdio. Als Liste, damit es env-/appsettings-überschreibbar ist.</summary>
    public List<string> ProbeMcpArgv { get; set; } =
        new() { "node", "/app/cli.js", "--headless", "--browser", "chromium", "--no-sandbox" };
```

(The test compares against `IReadOnlyList<string>`; a `List<string>` satisfies it.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DastOptionsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Dast/DastOptions.cs tests/Naudit.Tests/DastOptionsTests.cs
git commit -m "feat(dast): DastOptions.MaxProbeSteps + ProbeMcpArgv (stdio-MCP-Start)"
```

---

