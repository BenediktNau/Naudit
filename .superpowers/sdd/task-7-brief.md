### Task 7: DI wiring + settings

**Files:**
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (the `dastOptions.Enabled` block from PR 1)
- Modify: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`
- Test: `tests/Naudit.Tests/DastWiringTests.cs` (extend)

**Interfaces:**
- Consumes: PR-1 `IAppRunner` registration, the global `IChatClient`, the shared `IDockerClient`.
- Produces: `ISastAnalyzer` = `DastAnalyzer` registered when `dastOptions.Enabled`; `Naudit:Review:Dast:MaxProbeSteps` in the catalog.

- [ ] **Step 1: Write the failing test**

Append to `tests/Naudit.Tests/DastWiringTests.cs`:

```csharp
    [Fact]
    public void Dast_enabled_registersDastAnalyzer_amongSastAnalyzers()
    {
        var settings = BaseSettings();
        settings["Naudit:Review:Dast:Enabled"] = "true";
        using var provider = Build(settings);

        Assert.Contains(provider.GetServices<Naudit.Core.Abstractions.ISastAnalyzer>(),
            a => a.Name == "dast");
    }

    [Fact]
    public void Dast_disabled_registersNoDastAnalyzer()
    {
        using var provider = Build(BaseSettings());

        Assert.DoesNotContain(provider.GetServices<Naudit.Core.Abstractions.ISastAnalyzer>(),
            a => a.Name == "dast");
    }
```

(`GetServices<ISastAnalyzer>()` must resolve; ensure the test's service collection registers what `AddNauditInfrastructure` needs — mirror the existing `DastWiringTests` bootstrap. The DAST analyzer needs `IChatClient` + `IDockerClient` + `IAppRunner` in the container; all are registered when DAST is enabled.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~Dast_enabled_registersDastAnalyzer"`
Expected: FAIL — no `ISastAnalyzer` named "dast" is registered.

- [ ] **Step 3: Wire it**

In `src/Naudit.Infrastructure/DependencyInjection.cs`, inside the existing `if (dastOptions.Enabled)` block (after the `IAppRunner` + sweeper registrations from PR 1), add:

```csharp
            // DAST-Probing als weiterer ISastAnalyzer (läuft, sobald _analyzers nicht leer ist —
            // unabhängig von sastOptions.Enabled). Nutzt den GLOBALEN IChatClient (nie den
            // Author-Session-Router), wie DistillingReviewGuidelines.
            services.AddScoped<ISastAnalyzer>(sp => new DastAnalyzer(
                sp.GetRequiredService<IAppRunner>(),
                dastOptions,
                sp.GetRequiredService<IChatClient>(),
                sp.GetRequiredService<IDockerClient>(),
                sp.GetRequiredService<ILoggerFactory>()));
```

Add `using Naudit.Core.Abstractions;` and `using Microsoft.Extensions.AI;` if not already present.

- [ ] **Step 4: Add the catalog key**

In `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`, with the other `Naudit:Review:Dast:*` keys:

```csharp
        new("Naudit:Review:Dast:MaxProbeSteps", false),
```

(`ProbeMcpArgv` is list-shaped ⇒ env-only, not in the catalog — `Projects` precedent.)

- [ ] **Step 5: Run tests + full suite**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DastWiringTests`
Expected: PASS. Then `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx` — 712.

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/DependencyInjection.cs src/Naudit.Infrastructure/Settings/SettingsCatalog.cs tests/Naudit.Tests/DastWiringTests.cs
git commit -m "feat(dast): DastAnalyzer als ISastAnalyzer verdrahtet + MaxProbeSteps im Katalog"
```

---

