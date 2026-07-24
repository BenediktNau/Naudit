# DAST PR 1 — App-Runner (build → run → healthcheck → guaranteed teardown)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Naudit can build the PR's own `Dockerfile`, start the resulting app as a sibling container in an egress-less review network, wait until it serves HTTP, hand back a reachable URL, and tear everything down again — with no LLM involved and no way for the app container to reach the internet, the host, or another review. Every interaction with the review network runs through the Docker socket, so the runner behaves identically whether Naudit itself runs as a container or as a bare process (decision 2026-07-24 in the spec).

**Architecture:** A new `IAppRunner` seam in `src/Naudit.Infrastructure/Dast/` drives the existing `IDockerClient` (from the session sandbox, now on main), which grows the operations it lacks: build an image from a tar'd build context, pull the probe image, create/remove a user-defined `internal` network, remove images, and list networks/images for orphan cleanup. Reachability is exclusively socket-based: a **probe container** (the Playwright-MCP image that PR 2 needs anyway) is started inside the review network, and the healthcheck runs as `docker exec` in it — `ExecAsync`, the same primitive the session sandbox uses for CLI runs. Naudit itself never joins the network and **no port is published anywhere**. Everything is opt-in (`Naudit:Review:Dast:Enabled`) *and* per-project (`Naudit:Review:Dast:Projects` allowlist — empty means no project), fail-open (any failure ⇒ `null`, never an exception at the caller), and teardown is guaranteed in a `finally`/`IAsyncDisposable`.

**Tech Stack:** .NET 10, `System.Formats.Tar`, hand-rolled Docker Engine API over the Unix socket (no new NuGet), xUnit with a `FakeDockerClient` (no real Docker in CI).

**Spec:** `docs/superpowers/specs/2026-07-19-dast-design.md` — authoritative requirements, already committed on this branch. Branch: `feat/dast-app-runner` (off main, which now carries the merged session sandbox #67). This is **PR 1 of 2**; PR 2 (`DastAnalyzer : ISastAnalyzer`, Playwright-MCP probing, `FindingCategory.Dast`) is a separate later plan and **nothing in this PR calls the runner yet**.

## Global Constraints

- Solution file is `Naudit.slnx` — `dotnet build Naudit.slnx`. Single class: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter <Name>`.
- **Run the full suite with `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`.** Without it 2–7 `WebApplicationFactory` tests fail randomly on this machine (inotify instance limit 128). Baseline on this branch: **674/674 green**.
- **Core rule:** `Naudit.Core` depends only on `Microsoft.Extensions.AI.Abstractions`. This PR adds exactly **one** Core member — `IReviewWorkspace.ProjectId` (a `string`, no new dependency) — because the per-project allowlist has to be enforced inside the runner, and `ISastAnalyzer.AnalyzeAsync` never sees the `ReviewRequest`. `FindingCategory.Dast` is **not** part of this PR (no findings are produced yet).
- **No real Docker in CI.** All runner/sweeper tests go through `FakeDockerClient`; engine-API round-trips are opt-in integration tests gated on `NAUDIT_TEST_DOCKER=1` (pattern: `SocketDockerClientTests`).
- **Fail-open everywhere:** every failure path (not applicable, no Dockerfile, context too large, build failed, healthcheck timeout, socket gone, budget exceeded) returns `null` after a full teardown. The only exception that leaves `RunAsync` is a **caller** cancellation (`ct`), never the internal time budget.
- **No Naudit secrets in the app container**: no environment beyond what the caller passes explicitly, no volume, no Docker socket, no host mounts. Ever.
- Naming: every DAST resource is prefixed `naudit-dast-` (`naudit-dast-img-<key>`, `naudit-dast-net-<key>`, `naudit-dast-app-<key>`, `naudit-dast-pw-<key>`) so the orphan sweeper can find leftovers by prefix. The pulled `ProbeImage` is deliberately **not** prefixed — it stays cached across reviews and is never swept.
- Code comments in German, docs in English. TDD: red → green → one commit per task.
- Config keys: scalars join `SettingsCatalog` (non-secret, DB-manageable); the list-shaped `Projects` stays env/appsettings-only, following the `ProjectTokens`/`Ui:Admins` precedent.

## File Structure

**New (`src/Naudit.Infrastructure/Dast/`)**

| File | Responsibility |
| --- | --- |
| `DastOptions.cs` | Bound from `Naudit:Review:Dast`; owns the `AppliesTo(projectId)` allowlist decision. |
| `IAppRunner.cs` | The seam: `IAppRunner` + `RunningApp` (an `IAsyncDisposable` handle carrying URL/network/container). |
| `DockerAppRunner.cs` | The only implementation: build → network → run app + probe container → exec health-poll → handle; teardown closure. |
| `WorkspaceTarPacker.cs` | Turns a checkout directory into a tar stream for the build context (skips `.git`, enforces a size cap). |
| `DastOrphanSweeper.cs` | `IHostedService`: removes `naudit-dast-*` containers/networks/images left behind by a crash. |

**Modified**

| File | Change |
| --- | --- |
| `src/Naudit.Core/Abstractions/IWorkspaceProvider.cs` | `IReviewWorkspace` gains `string ProjectId { get; }`. |
| `src/Naudit.Infrastructure/Sast/GitWorkspaceProvider.cs` | `GitWorkspace` carries the request's `ProjectId`. |
| `src/Naudit.Infrastructure/Docker/IDockerClient.cs` | Network/build/image operations; `ContainerRunSpec` gains optional `Network`/`Environment`/`Limits`, volume fields become nullable; new `ContainerLimits`, `DockerBuildResult`. |
| `src/Naudit.Infrastructure/Docker/SocketDockerClient.cs` | Implements the new operations. |
| `src/Naudit.Infrastructure/DependencyInjection.cs` | Binds `DastOptions`, registers one shared `IDockerClient` for sandbox **and** DAST, registers `IAppRunner` + sweeper when enabled. |
| `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs` | The scalar `Naudit:Review:Dast:*` keys. |
| `tests/Naudit.Tests/Fakes/FakeDockerClient.cs` | Records networks/images/builds, scriptable build failure. |
| `tests/Naudit.Tests/*AnalyzerTests.cs`, `WorkspaceContextCollectorTests.cs`, `Fakes/FakeWorkspaceProvider.cs` | Their `IReviewWorkspace` test doubles get the new member. |
| `docs/dast.md` (rewrite — ops guide already on main), `docs/deployment.md`, `CLAUDE.md` | Documentation. |

---

### Task 1: Workspace knows its project + `DastOptions` allowlist

**Files:**
- Modify: `src/Naudit.Core/Abstractions/IWorkspaceProvider.cs`
- Modify: `src/Naudit.Infrastructure/Sast/GitWorkspaceProvider.cs:54-60`
- Create: `src/Naudit.Infrastructure/Dast/DastOptions.cs`
- Modify: `tests/Naudit.Tests/Fakes/FakeWorkspaceProvider.cs`, `tests/Naudit.Tests/BetterleaksAnalyzerTests.cs:12`, `DotnetScaAnalyzerTests.cs:12`, `OsvScannerAnalyzerTests.cs:12`, `OpengrepAnalyzerTests.cs:12`, `WorkspaceContextCollectorTests.cs:26`
- Test: `tests/Naudit.Tests/DastOptionsTests.cs` (new)

**Interfaces:**
- Produces: `IReviewWorkspace.ProjectId` (string, non-null); `DastOptions` with `Enabled`, `Projects`, `DockerfilePath`, `AppPort`, `HealthPath`, `TimeBudget`, `HealthPollInterval`, `MemoryLimitMb`, `CpuLimit`, `PidsLimit`, `MaxContextMb`, `DockerSocketPath`, `ProbeImage` and `bool AppliesTo(string? projectId)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Naudit.Tests/DastOptionsTests.cs`:

```csharp
using Naudit.Infrastructure.Dast;
using Xunit;

namespace Naudit.Tests;

public class DastOptionsTests
{
    [Fact]
    public void AppliesTo_disabled_isFalse_evenForListedProject()
    {
        var options = new DastOptions { Enabled = false, Projects = { "acme/shop" } };

        Assert.False(options.AppliesTo("acme/shop"));
    }

    /// <summary>Leere Liste = kein Projekt (fail-closed): ein versehentlich global gesetzter
    /// Schalter führt so noch keinen fremden PR-Code aus.</summary>
    [Fact]
    public void AppliesTo_enabledButEmptyAllowlist_isFalse()
    {
        var options = new DastOptions { Enabled = true };

        Assert.False(options.AppliesTo("acme/shop"));
    }

    [Fact]
    public void AppliesTo_listedProject_isTrue_caseInsensitive_andTrimmed()
    {
        var options = new DastOptions { Enabled = true, Projects = { " Acme/Shop " } };

        Assert.True(options.AppliesTo("acme/shop"));
    }

    [Fact]
    public void AppliesTo_unlistedProject_isFalse()
    {
        var options = new DastOptions { Enabled = true, Projects = { "acme/shop" } };

        Assert.False(options.AppliesTo("acme/other"));
        Assert.False(options.AppliesTo(null));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DastOptionsTests`
Expected: FAIL — `error CS0246: The type or namespace name 'DastOptions' could not be found`.

- [ ] **Step 3: Write the implementation**

Create `src/Naudit.Infrastructure/Dast/DastOptions.cs`:

```csharp
namespace Naudit.Infrastructure.Dast;

/// <summary>Section Naudit:Review:Dast — dynamische Prüfung an der laufenden App. Zwei Schalter
/// hintereinander, weil hier fremder PR-Code gebaut UND ausgeführt wird: der globale Enabled-Schalter
/// und eine Projekt-Allowlist. Leere Allowlist ⇒ kein Projekt (fail-closed).</summary>
public sealed class DastOptions
{
    /// <summary>Globaler Kill-Switch. Bewusst unabhängig von Naudit:Ai:SessionSandbox — andere
    /// Risikoklasse (eigene Abo-Container vs. fremder PR-Code).</summary>
    public bool Enabled { get; set; }

    /// <summary>Freigeschaltete Projekte ("owner/repo" bzw. GitLab-Projekt-Id). Listenförmig ⇒
    /// env/appsettings-only wie ProjectTokens, nicht DB-verwaltet.</summary>
    public List<string> Projects { get; set; } = new();

    /// <summary>Dockerfile relativ zum Checkout-Root; fehlt es, ist DAST für diesen PR nicht anwendbar.</summary>
    public string DockerfilePath { get; set; } = "Dockerfile";

    /// <summary>Port, auf dem die getestete App im Container lauscht.</summary>
    public int AppPort { get; set; } = 8080;

    /// <summary>HTTP-Pfad für den Healthcheck.</summary>
    public string HealthPath { get; set; } = "/";

    /// <summary>Deckel über Build + Start + Healthcheck zusammen.</summary>
    public TimeSpan TimeBudget { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Abstand zwischen zwei Healthcheck-Versuchen.</summary>
    public TimeSpan HealthPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public int MemoryLimitMb { get; set; } = 1024;
    public double CpuLimit { get; set; } = 1.0;
    public int PidsLimit { get; set; } = 256;

    /// <summary>Obergrenze für den Build-Kontext (der komplett über den Socket in den Daemon wandert).</summary>
    public int MaxContextMb { get; set; } = 200;

    public string DockerSocketPath { get; set; } = "/var/run/docker.sock";

    /// <summary>Image des Probe-Containers im Review-Netz (PR 1: Healthcheck per exec, PR 2:
    /// Playwright-MCP-Server). Wird bei Bedarf gepullt; bewusst nicht naudit-dast-präfixiert,
    /// damit es als Cache über Reviews hinweg stehen bleibt.</summary>
    public string ProbeImage { get; set; } = "mcr.microsoft.com/playwright/mcp:latest";

    /// <summary>Darf für dieses Projekt gebaut/gestartet werden? Beide Schalter müssen zustimmen.</summary>
    public bool AppliesTo(string? projectId)
        => Enabled
           && !string.IsNullOrWhiteSpace(projectId)
           && Projects.Any(p => string.Equals(p.Trim(), projectId.Trim(), StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DastOptionsTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Add `ProjectId` to the workspace seam**

In `src/Naudit.Core/Abstractions/IWorkspaceProvider.cs`, replace the interface body:

```csharp
/// <summary>Handle auf den ausgecheckten Quellbaum; DisposeAsync räumt das Temp-Verzeichnis auf.</summary>
public interface IReviewWorkspace : IAsyncDisposable
{
    string RootPath { get; }

    /// <summary>Projekt des Reviews (GitLab-Projekt-Id bzw. "owner/repo"). Analyzer bekommen den
    /// ReviewRequest nicht, brauchen die Kennung aber für projektweise Freigaben (DAST-Allowlist) —
    /// der Checkout kennt sie ohnehin aus dem Request.</summary>
    string ProjectId { get; }
}
```

In `src/Naudit.Infrastructure/Sast/GitWorkspaceProvider.cs`, the nested class and its construction:

```csharp
    private sealed class GitWorkspace(string root, string projectId) : IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public string ProjectId { get; } = projectId;
```

and at the `return new GitWorkspace(...)` site in `CheckoutAsync`, pass `request.ProjectId` as the second argument.

- [ ] **Step 6: Fix the test doubles**

In each of `BetterleaksAnalyzerTests.cs`, `DotnetScaAnalyzerTests.cs`, `OsvScannerAnalyzerTests.cs`, `OpengrepAnalyzerTests.cs` the private double becomes:

```csharp
    private sealed class Ws(string root) : Naudit.Core.Abstractions.IReviewWorkspace
    {
        public string RootPath => root;
        public string ProjectId => "acme/test";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
```

(keep each file's existing `DisposeAsync` body; only the `ProjectId` line is new). Apply the same one-line addition to `WorkspaceContextCollectorTests.TestWorkspace` and to the workspace returned by `Fakes/FakeWorkspaceProvider.cs` — there use the request's own value: `new FakeWorkspace(root, request.ProjectId)` if it takes the request, otherwise a constant `"acme/test"`.

- [ ] **Step 7: Build and run the full suite**

Run: `dotnet build Naudit.slnx && DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS — 678 tests (674 baseline + 4 new), 0 failures.

- [ ] **Step 8: Commit**

```bash
git add src/Naudit.Core/Abstractions/IWorkspaceProvider.cs src/Naudit.Infrastructure/Sast/GitWorkspaceProvider.cs src/Naudit.Infrastructure/Dast/DastOptions.cs tests/Naudit.Tests
git commit -m "feat(dast): DastOptions mit Projekt-Allowlist + ProjectId am Review-Workspace"
```

---

### Task 2: Docker seam — the egress-less review network

**Files:**
- Modify: `src/Naudit.Infrastructure/Docker/IDockerClient.cs`
- Modify: `src/Naudit.Infrastructure/Docker/SocketDockerClient.cs`
- Modify: `tests/Naudit.Tests/Fakes/FakeDockerClient.cs`
- Test: `tests/Naudit.Tests/SocketDockerClientTests.cs` (new opt-in test method)

**Interfaces:**
- Consumes: `IDockerClient` as it stands on main (`SendAsync`/`EnsureAsync`/`ReadJsonAsync` helpers inside `SocketDockerClient`).
- Produces: `CreateNetworkAsync(name)`, `RemoveNetworkAsync(name)`, `ListNetworksAsync(namePrefix) -> IReadOnlyList<string>`. `FakeDockerClient.Networks` (`HashSet<string>`). Connect/disconnect operations are deliberately absent: containers join the network at start (`ContainerRunSpec.Network`, Task 3) and Naudit itself never joins — all reachability runs over `docker exec` (decision 2026-07-24).

- [ ] **Step 1: Write the failing test**

Append to `tests/Naudit.Tests/SocketDockerClientTests.cs` (inside the class):

```csharp
    /// <summary>Netz-Lebenszyklus gegen echtes Docker: internes Netz anlegen, listen, entfernen —
    /// die Naht, auf der der DAST-App-Runner aufsetzt. Container betreten das Netz beim Start
    /// (ContainerRunSpec.Network, Task 3); ein nachträgliches Connect gibt es bewusst nicht.</summary>
    [Fact]
    public async Task NetworkLifecycle_create_list_remove()
    {
        if (!Enabled) return; // ohne Docker-Env: übersprungen

        using var docker = new SocketDockerClient(SocketPath);
        var network = $"naudit-dast-net-{Guid.NewGuid():N}";
        try
        {
            await docker.CreateNetworkAsync(network);
            await docker.CreateNetworkAsync(network); // idempotent: gibt es schon
            Assert.Contains(network, await docker.ListNetworksAsync("naudit-dast-"));
        }
        finally
        {
            await docker.RemoveNetworkAsync(network);
            await docker.RemoveNetworkAsync(network); // idempotent: schon weg
        }

        Assert.DoesNotContain(network, await docker.ListNetworksAsync("naudit-dast-"));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet build Naudit.slnx`
Expected: FAIL — `error CS1061: 'SocketDockerClient' does not contain a definition for 'CreateNetworkAsync'` (and the sibling members). This test class only executes against real Docker; the compile failure is the red signal here.

- [ ] **Step 3: Extend the interface**

In `src/Naudit.Infrastructure/Docker/IDockerClient.cs`, add to the interface (below `ListContainersAsync`):

```csharp
    /// <summary>Legt ein benutzerdefiniertes Netz an — internal (kein Egress: der getestete Code
    /// erreicht weder Internet noch Host). Container betreten das Netz beim Start
    /// (ContainerRunSpec.Network); ein nachträgliches Connect gibt es bewusst nicht — jede
    /// Erreichbarkeit läuft über docker exec. Existiert es schon, ist das kein Fehler.</summary>
    Task CreateNetworkAsync(string name, CancellationToken ct = default);

    /// <summary>Entfernt ein Netz; bereits weg ⇒ kein Fehler (Teardown ist best-effort).</summary>
    Task RemoveNetworkAsync(string name, CancellationToken ct = default);

    /// <summary>Namen aller Netze mit diesem Präfix — für den Orphan-Sweeper.</summary>
    Task<IReadOnlyList<string>> ListNetworksAsync(string namePrefix, CancellationToken ct = default);
```

- [ ] **Step 4: Implement in `SocketDockerClient`**

Add to `src/Naudit.Infrastructure/Docker/SocketDockerClient.cs` (above `Dispose`):

```csharp
    public async Task CreateNetworkAsync(string name, CancellationToken ct = default)
    {
        // Internal = kein Egress. Kein Attachable: niemand hängt sich nachträglich hinein —
        // Container betreten das Netz beim Start, Naudit spricht nur über docker exec hinein.
        var body = new { Name = name, Internal = true, CheckDuplicate = true };
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "/networks/create")
        { Content = JsonContent.Create(body, options: OutJsonOpts) }, ct);
        await EnsureAsync(resp, ct, HttpStatusCode.Conflict); // 409 = gibt es schon
    }

    public async Task RemoveNetworkAsync(string name, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"/networks/{Uri.EscapeDataString(name)}"), ct);
        await EnsureAsync(resp, ct, HttpStatusCode.NotFound);
    }

    public async Task<IReadOnlyList<string>> ListNetworksAsync(string namePrefix, CancellationToken ct = default)
    {
        // Der name-Filter der Engine matcht Substrings — Präfix darum client-seitig nachprüfen
        // (gleiches Muster wie ListContainersAsync).
        var filters = Uri.EscapeDataString($"{{\"name\":[\"{namePrefix}\"]}}");
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/networks?filters={filters}"), ct);
        await EnsureAsync(resp, ct);
        var entries = await ReadJsonAsync<List<NetworkListEntry>>(resp, ct);
        return entries
            .Select(e => e.Name ?? "")
            .Where(n => n.StartsWith(namePrefix, StringComparison.Ordinal))
            .ToList();
    }
```

and next to the other private response records at the bottom of the file:

```csharp
    private sealed record NetworkListEntry(string? Name);
```

- [ ] **Step 5: Extend `FakeDockerClient`**

In `tests/Naudit.Tests/Fakes/FakeDockerClient.cs`, add the state and the three methods:

```csharp
    public HashSet<string> Networks { get; } = new();

    public virtual Task CreateNetworkAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"netcreate:{name}");
        Networks.Add(name);
        return Task.CompletedTask;
    }

    public Task RemoveNetworkAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"netrm:{name}");
        Networks.Remove(name);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListNetworksAsync(string namePrefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Networks
            .Where(n => n.StartsWith(namePrefix, StringComparison.Ordinal)).ToList());
```

(`CreateNetworkAsync` is `virtual` from the start — Task 5 subclasses it; that also requires changing the class declaration from `internal sealed class FakeDockerClient` to `internal class FakeDockerClient` now.)

- [ ] **Step 6: Build and run the suite**

Run: `dotnet build Naudit.slnx && DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS — 678 tests (the new integration test returns early without `NAUDIT_TEST_DOCKER=1`).

Optional, if a local Docker is available:
Run: `NAUDIT_TEST_DOCKER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SocketDockerClientTests`
Expected: PASS — 2 tests, including the new network lifecycle.

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Infrastructure/Docker tests/Naudit.Tests
git commit -m "feat(dast): IDockerClient um egress-loses Review-Netz erweitert (create/connect/disconnect/remove/list)"
```

---

### Task 3: Docker seam — build the PR image from a tar'd context

**Files:**
- Create: `src/Naudit.Infrastructure/Dast/WorkspaceTarPacker.cs`
- Modify: `src/Naudit.Infrastructure/Docker/IDockerClient.cs`, `src/Naudit.Infrastructure/Docker/SocketDockerClient.cs`
- Modify: `tests/Naudit.Tests/Fakes/FakeDockerClient.cs`
- Test: `tests/Naudit.Tests/WorkspaceTarPackerTests.cs` (new)

**Interfaces:**
- Produces: `WorkspaceTarPacker.PackAsync(string rootPath, int maxContextMb, CancellationToken) -> Task<Stream?>` (null = over the cap); `IDockerClient.BuildImageAsync(string tag, Stream tarContext, string dockerfilePath, CancellationToken) -> Task<DockerBuildResult>`; `PullImageAsync(string reference)`; `RemoveImageAsync(string tag)`; `ListImagesAsync(string tagPrefix) -> IReadOnlyList<string>`; `DockerBuildResult(bool Success, string Log)`; `ContainerRunSpec` extended with `Network`/`Environment`/`Limits`/`Entrypoint` and nullable volume fields; `ContainerLimits(int MemoryMb, double Cpus, int PidsLimit)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Naudit.Tests/WorkspaceTarPackerTests.cs`:

```csharp
using System.Formats.Tar;
using Naudit.Infrastructure.Dast;
using Xunit;

namespace Naudit.Tests;

public class WorkspaceTarPackerTests
{
    private static string NewCheckout(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"naudit-tar-{Guid.NewGuid():N}");
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return root;
    }

    private static async Task<List<string>> EntryNamesAsync(Stream tar)
    {
        var names = new List<string>();
        await using var reader = new TarReader(tar);
        while (await reader.GetNextEntryAsync() is { } entry)
            names.Add(entry.Name);
        return names;
    }

    [Fact]
    public async Task Pack_containsRepoFiles_relativeToRoot_andSkipsGitDirectory()
    {
        var root = NewCheckout(
            ("Dockerfile", "FROM scratch"),
            (Path.Combine("src", "app.cs"), "class A {}"),
            (Path.Combine(".git", "config"), "[core]"));

        var tar = await WorkspaceTarPacker.PackAsync(root, maxContextMb: 10);

        Assert.NotNull(tar);
        var names = await EntryNamesAsync(tar!);
        Assert.Contains("Dockerfile", names);
        Assert.Contains("src/app.cs", names);
        Assert.DoesNotContain(names, n => n.StartsWith(".git", StringComparison.Ordinal));
    }

    /// <summary>Der Kontext wandert komplett über den Socket in den Daemon — ein Riesen-Checkout
    /// wird abgelehnt (null) statt hunderte MB zu schieben.</summary>
    [Fact]
    public async Task Pack_overSizeCap_returnsNull()
    {
        var root = NewCheckout(("big.bin", new string('x', 2 * 1024 * 1024)));

        Assert.Null(await WorkspaceTarPacker.PackAsync(root, maxContextMb: 1));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter WorkspaceTarPackerTests`
Expected: FAIL — `error CS0246: The type or namespace name 'WorkspaceTarPacker' could not be found`.

- [ ] **Step 3: Write the packer**

Create `src/Naudit.Infrastructure/Dast/WorkspaceTarPacker.cs`:

```csharp
using System.Formats.Tar;

namespace Naudit.Infrastructure.Dast;

/// <summary>Packt den Checkout als Tar-Strom für den Docker-Build-Kontext. Nötig, weil die Engine
/// den Kontext aus dem Request-Body liest — der Daemon sieht Naudits Dateisystem nicht (Sibling-
/// Container). `.git` bleibt draußen (reiner Ballast, oft der größte Anteil), und ab MaxContextMb
/// bricht der Packer ab, statt hunderte MB durch den Socket zu schieben.</summary>
public static class WorkspaceTarPacker
{
    public static async Task<Stream?> PackAsync(string rootPath, int maxContextMb, CancellationToken ct = default)
    {
        var limit = (long)maxContextMb * 1024 * 1024;
        var buffer = new MemoryStream();
        await using (var tar = new TarWriter(buffer, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                if (relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal))
                    continue;
                if (buffer.Length + SafeLength(file) > limit)
                {
                    await buffer.DisposeAsync();
                    return null;
                }
                // Sonderdateien (tote Symlinks, Sockets, Rechte) dürfen den Kontext nicht kippen.
                try { await tar.WriteEntryAsync(file, relative, ct); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        buffer.Position = 0;
        return buffer;
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (IOException) { return 0; }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter WorkspaceTarPackerTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Extend the Docker seam with build/image operations**

In `src/Naudit.Infrastructure/Docker/IDockerClient.cs`, add to the interface:

```csharp
    /// <summary>docker build: Kontext als Tar-Strom, Dockerfile-Pfad relativ dazu. Ein
    /// fehlgeschlagener Build ist KEIN Docker-Problem, sondern ein Ergebnis (Success=false, Log) —
    /// nur Transport-/API-Fehler werfen DockerUnavailableException.</summary>
    Task<DockerBuildResult> BuildImageAsync(string tag, Stream tarContext, string dockerfilePath,
        CancellationToken ct = default);

    /// <summary>docker pull: holt ein Referenz-Image (Probe-Container). Schon vorhanden ⇒ schneller
    /// No-op der Engine. Nicht pullbar (Registry weg, Tippfehler) ⇒ DockerUnavailableException —
    /// der Runner behandelt das fail-open.</summary>
    Task PullImageAsync(string reference, CancellationToken ct = default);

    /// <summary>Entfernt ein Image; schon weg/noch in Benutzung ⇒ kein Fehler (best-effort-Teardown).</summary>
    Task RemoveImageAsync(string tag, CancellationToken ct = default);

    /// <summary>Tags aller Images mit diesem Präfix — für den Orphan-Sweeper.</summary>
    Task<IReadOnlyList<string>> ListImagesAsync(string tagPrefix, CancellationToken ct = default);
```

and replace the `ContainerRunSpec` record plus add the two new records:

```csharp
/// <summary>Volume-Felder sind optional: die Session-Sandbox mountet ihr Credential-Volume, ein
/// DAST-Container bekommt keins. Network/Environment/Limits sind ebenfalls optional — ohne sie
/// verhält sich RunDetachedAsync exakt wie vorher.</summary>
public sealed record ContainerRunSpec(string Name, string Image, string? VolumeName, string? VolumeTarget,
    IReadOnlyList<string> Command)
{
    /// <summary>Benanntes Netz, an dem der Container startet (DAST-Review-Netz).</summary>
    public string? Network { get; init; }

    /// <summary>Env des Containers. Für DAST gilt: nie Naudit-Secrets, nur explizit Gesetztes.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Eindämmung für fremden Code; null = keine Grenzen (Session-Sandbox unverändert).</summary>
    public ContainerLimits? Limits { get; init; }

    /// <summary>Überschreibt den ENTRYPOINT des Images (Probe-Container: ["sleep","infinity"] —
    /// er lebt passiv und wird nur per docker exec benutzt). null = ENTRYPOINT des Images gilt.</summary>
    public IReadOnlyList<string>? Entrypoint { get; init; }
}

/// <summary>Grenzen für Container mit fremdem Code: Speicher/CPU/PID-Deckel gegen Fork-Bomb und OOM,
/// dazu alle Capabilities weg und keine Privilege-Eskalation.</summary>
public sealed record ContainerLimits(int MemoryMb, double Cpus, int PidsLimit);

/// <summary>Ergebnis eines Builds: Success=false heißt "dieser PR lässt sich nicht bauen"
/// (kein Fehlerfall der Naht), Log trägt die letzten Zeilen der Engine-Ausgabe.</summary>
public sealed record DockerBuildResult(bool Success, string Log);
```

- [ ] **Step 6: Implement build/image in `SocketDockerClient`**

Add to `src/Naudit.Infrastructure/Docker/SocketDockerClient.cs`:

```csharp
    public async Task<DockerBuildResult> BuildImageAsync(string tag, Stream tarContext, string dockerfilePath,
        CancellationToken ct = default)
    {
        var url = $"/build?t={Uri.EscapeDataString(tag)}&dockerfile={Uri.EscapeDataString(dockerfilePath)}" +
                  "&rm=true&forcerm=true&q=true";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StreamContent(tarContext) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
        using var resp = await SendAsync(req, ct, HttpCompletionOption.ResponseHeadersRead);
        await EnsureAsync(resp, ct);

        // Der Body ist ein JSON-Lines-Strom; ein {"error":…}-Objekt darin bedeutet Build-Fehler,
        // der HTTP-Status bleibt dabei 200. Deshalb bis zum Ende lesen und auswerten.
        var log = new StringBuilder();
        var failed = false;
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
                continue;
            log.AppendLine(line);
            if (line.Contains("\"error\"", StringComparison.Ordinal))
                failed = true;
        }
        var text = log.ToString();
        return new DockerBuildResult(!failed, text.Length > 2000 ? text[^2000..] : text);
    }

    public async Task PullImageAsync(string reference, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"/images/create?fromImage={Uri.EscapeDataString(reference)}"), ct,
            HttpCompletionOption.ResponseHeadersRead);
        await EnsureAsync(resp, ct);

        // Auch hier JSON-Lines mit möglichem {"error":…} bei HTTP 200 — Strom leeren und prüfen.
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Contains("\"error\"", StringComparison.Ordinal))
                throw new DockerUnavailableException($"Image-Pull fehlgeschlagen: {reference}");
        }
    }

    public async Task RemoveImageAsync(string tag, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Delete,
            $"/images/{Uri.EscapeDataString(tag)}?force=true"), ct);
        await EnsureAsync(resp, ct, HttpStatusCode.NotFound, HttpStatusCode.Conflict);
    }

    public async Task<IReadOnlyList<string>> ListImagesAsync(string tagPrefix, CancellationToken ct = default)
    {
        var filters = Uri.EscapeDataString($"{{\"reference\":[\"{tagPrefix}*\"]}}");
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/images/json?filters={filters}"), ct);
        await EnsureAsync(resp, ct);
        var entries = await ReadJsonAsync<List<ImageListEntry>>(resp, ct);
        return entries
            .SelectMany(e => e.RepoTags ?? [])
            .Where(t => t.StartsWith(tagPrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
```

next to the other private records:

```csharp
    private sealed record ImageListEntry(List<string>? RepoTags);
```

and replace `RunDetachedAsync` so the optional spec parts are honoured:

```csharp
    public async Task RunDetachedAsync(ContainerRunSpec spec, CancellationToken ct = default)
    {
        var hostConfig = new Dictionary<string, object?>();
        if (spec.VolumeName is not null && spec.VolumeTarget is not null)
            hostConfig["Binds"] = new[] { $"{spec.VolumeName}:{spec.VolumeTarget}" };
        if (spec.Network is not null)
            hostConfig["NetworkMode"] = spec.Network;
        if (spec.Limits is { } limits)
        {
            hostConfig["Memory"] = (long)limits.MemoryMb * 1024 * 1024;
            hostConfig["NanoCpus"] = (long)(limits.Cpus * 1_000_000_000);
            hostConfig["PidsLimit"] = limits.PidsLimit;
            hostConfig["CapDrop"] = new[] { "ALL" };
            hostConfig["SecurityOpt"] = new[] { "no-new-privileges" };
        }

        var create = new Dictionary<string, object?> { ["Image"] = spec.Image, ["HostConfig"] = hostConfig };
        if (spec.Command.Count > 0)
            create["Cmd"] = spec.Command;               // leer ⇒ CMD/ENTRYPOINT des Images gilt
        if (spec.Entrypoint is { Count: > 0 })
            create["Entrypoint"] = spec.Entrypoint;     // Probe-Container: ["sleep","infinity"]
        if (spec.Environment is { Count: > 0 })
            create["Env"] = spec.Environment.Select(kv => $"{kv.Key}={kv.Value}").ToArray();

        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"/containers/create?name={Uri.EscapeDataString(spec.Name)}")
        { Content = JsonContent.Create(create, options: OutJsonOpts) }, ct);
        // 409 = Name existiert bereits (Race zweier EnsureRunning) — dann genügt der Start.
        await EnsureAsync(resp, ct, HttpStatusCode.Conflict);
        await StartAsync(spec.Name, ct);
    }
```

- [ ] **Step 7: Extend `FakeDockerClient`**

In `tests/Naudit.Tests/Fakes/FakeDockerClient.cs` add:

```csharp
    public List<(string Tag, string Dockerfile, long ContextBytes)> Builds { get; } = new();
    public HashSet<string> Images { get; } = new();
    public bool NextBuildFails { get; set; }

    public async Task<DockerBuildResult> BuildImageAsync(string tag, Stream tarContext, string dockerfilePath,
        CancellationToken ct = default)
    {
        Calls.Add($"build:{tag}");
        var buffer = new MemoryStream();
        await tarContext.CopyToAsync(buffer, ct);
        Builds.Add((tag, dockerfilePath, buffer.Length));
        if (NextBuildFails)
        {
            NextBuildFails = false;
            return new DockerBuildResult(false, "fake: build failed");
        }
        Images.Add(tag);
        return new DockerBuildResult(true, "");
    }

    public Task RemoveImageAsync(string tag, CancellationToken ct = default)
    {
        Calls.Add($"rmimg:{tag}");
        Images.Remove(tag);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListImagesAsync(string tagPrefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Images
            .Where(i => i.StartsWith(tagPrefix, StringComparison.Ordinal)).ToList());

    public List<string> PulledImages { get; } = new();

    public Task PullImageAsync(string reference, CancellationToken ct = default)
    {
        Calls.Add($"pull:{reference}");
        PulledImages.Add(reference);
        Images.Add(reference);
        return Task.CompletedTask;
    }
```

Two adjustments to the existing `RunDetachedAsync` in the fake, forced by the spec change: `Volumes.Add(spec.VolumeName)` needs a null guard (`if (spec.VolumeName is not null) …`), and Task 4 must inspect **both** run specs (app + probe), so record every spec:

```csharp
    public List<ContainerRunSpec> RunSpecs { get; } = new();   // neben LastRunSpec: RunSpecs.Add(spec);
```

- [ ] **Step 8: Build and run the suite**

Run: `dotnet build Naudit.slnx && DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS — 680 tests, 0 failures (session-sandbox tests still green: the spec change is source-compatible).

- [ ] **Step 9: Commit**

```bash
git add src/Naudit.Infrastructure tests/Naudit.Tests
git commit -m "feat(dast): Image-Build aus getartem Checkout + Container-Limits/Netz in der Docker-Naht"
```

---

### Task 4: `DockerAppRunner` — the happy path

**Files:**
- Create: `src/Naudit.Infrastructure/Dast/IAppRunner.cs`
- Create: `src/Naudit.Infrastructure/Dast/DockerAppRunner.cs`
- Test: `tests/Naudit.Tests/DockerAppRunnerTests.cs` (new)

**Interfaces:**
- Consumes: `IDockerClient` (Tasks 2+3, incl. `ExecAsync` from the session sandbox), `DastOptions` (Task 1), `IReviewWorkspace.ProjectId` (Task 1).
- Produces: `IAppRunner.RunAsync(IReviewWorkspace workspace, CancellationToken ct = default) -> Task<RunningApp?>`; `RunningApp` with `InternalUrl`, `NetworkName`, `ContainerName`, `ProbeContainerName` and an idempotent `DisposeAsync`; constants `DockerAppRunner.NamePrefix = "naudit-dast-"` and `DockerAppRunner.HealthProbeArgv(url)` (the exec-based health probe).

- [ ] **Step 1: Write the failing test**

Create `tests/Naudit.Tests/DockerAppRunnerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure.Dast;
using Naudit.Infrastructure.Docker;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class DockerAppRunnerTests
{
    private const string Project = "acme/shop";

    private sealed class Ws(string root) : IReviewWorkspace
    {
        public string RootPath => root;
        public string ProjectId => Project;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Checkout mit Dockerfile; Rückgabe ist der Root-Pfad.</summary>
    private static IReviewWorkspace Checkout(bool withDockerfile = true)
    {
        var root = Path.Combine(Path.GetTempPath(), $"naudit-dast-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        if (withDockerfile)
            File.WriteAllText(Path.Combine(root, "Dockerfile"), "FROM scratch\n");
        return new Ws(root);
    }

    private static DastOptions Options() => new()
    {
        Enabled = true,
        Projects = { Project },
        HealthPollInterval = TimeSpan.FromMilliseconds(1),
        TimeBudget = TimeSpan.FromSeconds(5),
    };

    private static (DockerAppRunner Runner, FakeDockerClient Docker) Create(
        DastOptions? options = null, FakeDockerClient? docker = null)
    {
        docker ??= new FakeDockerClient();
        return (new DockerAppRunner(docker, options ?? Options(),
            NullLogger<DockerAppRunner>.Instance), docker);
    }

    [Fact]
    public async Task Run_buildsRunsAndReturnsInternalUrl_reachedOnlyViaExec()
    {
        var (runner, docker) = Create();

        await using var app = await runner.RunAsync(Checkout());

        Assert.NotNull(app);
        Assert.StartsWith("naudit-dast-net-", app!.NetworkName);
        Assert.StartsWith("naudit-dast-app-", app.ContainerName);
        Assert.StartsWith("naudit-dast-pw-", app.ProbeContainerName);
        Assert.Equal($"http://{app.ContainerName}:8080/", app.InternalUrl);

        // Reihenfolge: bauen, Netz anlegen, Probe-Image pullen, App- und Probe-Container starten.
        var relevant = docker.Calls.Where(c =>
            c.StartsWith("build:") || c.StartsWith("netcreate:") || c.StartsWith("pull:") || c.StartsWith("run:"))
            .Select(c => c.Split(':')[0]).ToList();
        Assert.Equal(["build", "netcreate", "pull", "run", "run"], relevant);

        // Healthcheck als exec im Probe-Container gegen die interne URL — Naudit betritt das Netz nie.
        var exec = Assert.Single(docker.Execs);
        Assert.Equal(app.ProbeContainerName, exec.Container);
        Assert.Contains(app.InternalUrl, exec.Argv);

        var build = Assert.Single(docker.Builds);
        Assert.Equal("Dockerfile", build.Dockerfile);
        Assert.True(build.ContextBytes > 0);
    }

    [Fact]
    public async Task Run_startsBothContainers_withLimits_andWithoutVolumeOrEnvironment()
    {
        var (runner, docker) = Create();

        await using var app = await runner.RunAsync(Checkout());

        var appSpec = docker.RunSpecs.Single(s => s.Name.StartsWith("naudit-dast-app-", StringComparison.Ordinal));
        Assert.Equal(app!.NetworkName, appSpec.Network);
        Assert.Null(appSpec.VolumeName);       // kein Volume: der Container darf nichts überdauern
        Assert.Null(appSpec.Environment);      // niemals Naudit-Secrets im getesteten Container
        Assert.Empty(appSpec.Command);         // CMD/ENTRYPOINT des gebauten Images gilt
        Assert.Equal(1024, appSpec.Limits!.MemoryMb);
        Assert.Equal(256, appSpec.Limits.PidsLimit);

        var probeSpec = docker.RunSpecs.Single(s => s.Name.StartsWith("naudit-dast-pw-", StringComparison.Ordinal));
        Assert.Equal(app.NetworkName, probeSpec.Network);
        Assert.Equal(["sleep", "infinity"], probeSpec.Entrypoint);   // lebt passiv, wird nur per exec benutzt
        Assert.NotNull(probeSpec.Limits);      // auch der Browser-Container bleibt gedeckelt
    }

    [Fact]
    public async Task Run_projectNotOnAllowlist_returnsNull_withoutTouchingDocker()
    {
        var options = Options();
        options.Projects.Clear();
        options.Projects.Add("someone/else");
        var (runner, docker) = Create(options);

        Assert.Null(await runner.RunAsync(Checkout()));
        Assert.Empty(docker.Calls);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DockerAppRunnerTests`
Expected: FAIL — `error CS0246: The type or namespace name 'DockerAppRunner' could not be found`.

- [ ] **Step 3: Write the seam**

Create `src/Naudit.Infrastructure/Dast/IAppRunner.cs`:

```csharp
using Naudit.Core.Abstractions;

namespace Naudit.Infrastructure.Dast;

/// <summary>Baut die App des PRs aus deren eigenem Dockerfile, startet sie isoliert und liefert eine
/// erreichbare URL — oder null, wenn das für diesen PR nicht geht (nicht freigeschaltet, kein
/// Dockerfile, Build kaputt, kommt nicht hoch, Docker weg). Wirft nie wegen der Sache selbst.</summary>
public interface IAppRunner
{
    Task<RunningApp?> RunAsync(IReviewWorkspace workspace, CancellationToken ct = default);
}

/// <summary>Handle auf die laufende Test-App. DisposeAsync räumt App- und Probe-Container, Netz und
/// Image ab — idempotent, damit ein doppeltes Dispose (finally + using) nicht doppelt abräumt.</summary>
public sealed class RunningApp(string internalUrl, string networkName, string containerName,
    string probeContainerName, Func<ValueTask> teardown) : IAsyncDisposable
{
    /// <summary>URL im Review-Netz (Container-Name als Host) — nur vom Probe-/Playwright-Container
    /// erreichbar; Naudit, Host und Internet sehen sie nie.</summary>
    public string InternalUrl { get; } = internalUrl;

    public string NetworkName { get; } = networkName;
    public string ContainerName { get; } = containerName;

    /// <summary>Probe-Container im selben Netz — PR 1 nutzt ihn für den Healthcheck (exec),
    /// PR 2 spricht über ihn MCP (stdio via exec).</summary>
    public string ProbeContainerName { get; } = probeContainerName;

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await teardown();
    }
}
```

- [ ] **Step 4: Write the runner**

Create `src/Naudit.Infrastructure/Dast/DockerAppRunner.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure.Docker;

namespace Naudit.Infrastructure.Dast;

/// <summary>Führt fremden PR-Code aus — deshalb: eigenes internes Netz je Review (kein Egress,
/// keine veröffentlichten Ports, nirgends), Ressourcen- und Rechte-Grenzen an beiden Containern,
/// kein Volume, keine Naudit-Secrets, hartes Zeitbudget und garantierter Abbau. Naudit betritt das
/// Netz nie selbst: der Healthcheck läuft als docker exec im Probe-Container (dieselbe Primitive
/// wie die CLI-Läufe der Session-Sandbox) — dadurch verhält sich der Runner identisch, ob Naudit
/// im Container oder als nackter Prozess läuft. Jeder Fehlerpfad endet in Teardown + null
/// (fail-open).</summary>
public sealed class DockerAppRunner(
    IDockerClient docker,
    DastOptions options,
    ILogger<DockerAppRunner> logger,
    TimeProvider? time = null) : IAppRunner
{
    public const string NamePrefix = "naudit-dast-";

    public async Task<RunningApp?> RunAsync(IReviewWorkspace workspace, CancellationToken ct = default)
    {
        if (!options.AppliesTo(workspace.ProjectId))
            return null;

        if (!File.Exists(Path.Combine(workspace.RootPath, options.DockerfilePath)))
        {
            logger.LogInformation("DAST: kein {Dockerfile} im Checkout — übersprungen.", options.DockerfilePath);
            return null;
        }

        var key = Guid.NewGuid().ToString("N")[..8];
        var image = $"{NamePrefix}img-{key}";
        var network = $"{NamePrefix}net-{key}";
        var container = $"{NamePrefix}app-{key}";
        var probe = $"{NamePrefix}pw-{key}";

        // Ein Budget über Build + Start + Healthcheck; Teardown läuft danach OHNE Token weiter.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.TimeBudget);

        async ValueTask TearDownAsync()
        {
            await SafeAsync(() => docker.RemoveContainerAsync(probe, CancellationToken.None));
            await SafeAsync(() => docker.RemoveContainerAsync(container, CancellationToken.None));
            await SafeAsync(() => docker.RemoveNetworkAsync(network, CancellationToken.None));
            await SafeAsync(() => docker.RemoveImageAsync(image, CancellationToken.None));
            // Das ProbeImage bleibt absichtlich stehen — Cache über Reviews hinweg.
        }

        try
        {
            await using (var context = await WorkspaceTarPacker.PackAsync(workspace.RootPath, options.MaxContextMb, budget.Token))
            {
                if (context is null)
                {
                    logger.LogWarning("DAST: Build-Kontext größer als {Max} MB — übersprungen.", options.MaxContextMb);
                    return null;
                }
                var build = await docker.BuildImageAsync(image, context, options.DockerfilePath, budget.Token);
                if (!build.Success)
                {
                    logger.LogInformation("DAST: Build fehlgeschlagen — übersprungen. {Log}", build.Log);
                    await TearDownAsync();
                    return null;
                }
            }

            await docker.CreateNetworkAsync(network, budget.Token);
            await docker.PullImageAsync(options.ProbeImage, budget.Token); // schneller No-op, wenn schon da

            var limits = new ContainerLimits(options.MemoryLimitMb, options.CpuLimit, options.PidsLimit);
            await docker.RunDetachedAsync(
                new ContainerRunSpec(container, image, VolumeName: null, VolumeTarget: null, Command: [])
                { Network = network, Limits = limits }, budget.Token);
            // Probe-Container: lebt passiv (sleep) im selben Netz und wird nur per exec benutzt —
            // PR 1 für den Healthcheck, PR 2 als Playwright-MCP-Server (stdio via exec).
            await docker.RunDetachedAsync(
                new ContainerRunSpec(probe, options.ProbeImage, VolumeName: null, VolumeTarget: null, Command: [])
                { Network = network, Limits = limits, Entrypoint = ["sleep", "infinity"] }, budget.Token);

            var url = $"http://{container}:{options.AppPort}{options.HealthPath}";
            if (!await WaitForHealthyAsync(probe, url, budget.Token))
            {
                logger.LogInformation("DAST: App wurde nicht erreichbar ({Url}) — übersprungen.", url);
                await TearDownAsync();
                return null;
            }

            logger.LogInformation("DAST: App läuft unter {Url}.", url);
            return new RunningApp(url, network, container, probe, TearDownAsync);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TearDownAsync();
            throw; // echter Abbruch des Aufrufers wird durchgereicht
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DAST: App-Runner abgebrochen — Review läuft ohne dynamische Prüfung weiter.");
            await TearDownAsync();
            return null;
        }
    }

    /// <summary>HTTP-Probe als exec-Kommando im Probe-Container: Exit 0 = Webserver antwortet.
    /// Auch eine 404 zählt (beweist einen laufenden Server); 5xx kann eine noch startende App
    /// sein — weiter pollen. Node steckt im Playwright-Image.</summary>
    internal static IReadOnlyList<string> HealthProbeArgv(string url) =>
    [
        "node", "-e",
        "fetch(process.argv[1]).then(r=>process.exit(r.status<500?0:1),()=>process.exit(1))",
        url,
    ];

    private async Task<bool> WaitForHealthyAsync(string probe, string url, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await docker.ExecAsync(probe, HealthProbeArgv(url),
                environment: null, workingDirectory: "/", ct);
            if (result.ExitCode == 0)
                return true;

            try { await Task.Delay(options.HealthPollInterval, time ?? TimeProvider.System, ct); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private async Task SafeAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) { logger.LogWarning(ex, "DAST: Teilschritt des Abbaus fehlgeschlagen (best-effort)."); }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DockerAppRunnerTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Dast tests/Naudit.Tests/DockerAppRunnerTests.cs
git commit -m "feat(dast): DockerAppRunner — PR-Image bauen, isoliert starten, Healthcheck, URL"
```

---

### Task 5: Failure paths and guaranteed teardown

**Files:**
- Test: `tests/Naudit.Tests/DockerAppRunnerTests.cs` (extend)
- Modify (only if a test proves it necessary): `src/Naudit.Infrastructure/Dast/DockerAppRunner.cs`

**Interfaces:**
- Consumes: everything from Task 4; `FakeDockerClient.NextBuildFails`, `FakeDockerClient.Containers/Networks/Images`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Naudit.Tests/DockerAppRunnerTests.cs`:

```csharp
    [Fact]
    public async Task Run_withoutDockerfile_returnsNull_withoutTouchingDocker()
    {
        var (runner, docker) = Create();

        Assert.Null(await runner.RunAsync(Checkout(withDockerfile: false)));
        Assert.Empty(docker.Calls);
    }

    [Fact]
    public async Task Run_buildFails_returnsNull_andLeavesNothingBehind()
    {
        var docker = new FakeDockerClient { NextBuildFails = true };
        var (runner, _) = Create(docker: docker);

        Assert.Null(await runner.RunAsync(Checkout()));

        Assert.Empty(docker.Images);
        Assert.Empty(docker.Networks);
        Assert.Empty(docker.Containers);
    }

    /// <summary>App kommt nie hoch (Exec-Probe liefert dauerhaft Exit 1): Zeitbudget greift, danach
    /// ist die gesamte Review-Topologie wieder weg — nur das gecachte ProbeImage bleibt.</summary>
    [Fact]
    public async Task Run_appNeverBecomesHealthy_returnsNull_andTearsDownEverything()
    {
        var options = Options();
        options.TimeBudget = TimeSpan.FromMilliseconds(150);
        var docker = new FakeDockerClient { DefaultExecResult = new DockerExecResult(1, "", "") };
        var (runner, _) = Create(options, docker);

        Assert.Null(await runner.RunAsync(Checkout()));

        Assert.Empty(docker.Containers);
        Assert.Empty(docker.Networks);
        Assert.Equal([options.ProbeImage], docker.Images);   // Cache bleibt, naudit-dast-img-* ist weg
    }

    [Fact]
    public async Task Run_dockerUnavailableMidway_returnsNull_andTriesTeardownAnyway()
    {
        var docker = new ThrowOnNetworkCreate();
        var (runner, _) = Create(docker: docker);

        Assert.Null(await runner.RunAsync(Checkout()));

        Assert.Contains(docker.Calls, c => c.StartsWith("rmimg:"));
    }

    /// <summary>Erfolgsfall: Dispose räumt ab — und ein zweites Dispose räumt nicht erneut ab.</summary>
    [Fact]
    public async Task Dispose_tearsDownOnce_andIsIdempotent()
    {
        var (runner, docker) = Create();
        var app = await runner.RunAsync(Checkout());

        await app!.DisposeAsync();
        var afterFirst = docker.Calls.Count(c => c.StartsWith("rm:"));
        await app.DisposeAsync();

        Assert.Equal(2, afterFirst);   // App- UND Probe-Container, je genau einmal
        Assert.Equal(afterFirst, docker.Calls.Count(c => c.StartsWith("rm:")));
        Assert.Empty(docker.Containers);
        Assert.Empty(docker.Networks);
        Assert.DoesNotContain(docker.Images, i => i.StartsWith("naudit-dast-", StringComparison.Ordinal));
    }

    /// <summary>FakeDockerClient, der beim Netz-Anlegen wie eine tote Engine reagiert.</summary>
    private sealed class ThrowOnNetworkCreate : FakeDockerClient
    {
        public override Task CreateNetworkAsync(string name, CancellationToken ct = default)
            => throw new Naudit.Infrastructure.Docker.DockerUnavailableException("fake: engine down");
    }
```

The fake is already unsealed and `CreateNetworkAsync` already `virtual` (Task 2). Two small additions to `tests/Naudit.Tests/Fakes/FakeDockerClient.cs` for these tests: a persistent exec default (the queue only covers finite sequences),

```csharp
    public DockerExecResult? DefaultExecResult { get; set; }   // greift, wenn ExecResults leer ist
```

used as the fallback in `ExecAsync` (`ExecResults.Count > 0 ? ExecResults.Dequeue() : DefaultExecResult ?? new DockerExecResult(0, "", "")`).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DockerAppRunnerTests`
Expected: FAIL — the new tests fail (or the file does not compile until `FakeDockerClient` is unsealed). Fix the sealing/`virtual` first, then re-run: the remaining failures must be assertion failures in the new tests, not compile errors.

- [ ] **Step 3: Make them pass**

The implementation from Task 4 already covers every one of these paths. If a test fails, fix `DockerAppRunner` — never the test — with the smallest change that satisfies it. Two likely gaps:

- teardown must run even when `BuildImageAsync` reports failure (image may exist as a dangling tag);
- `TearDownAsync` must use `CancellationToken.None`, otherwise an expired budget cancels the cleanup it is supposed to trigger.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DockerAppRunnerTests`
Expected: PASS (8 tests).

- [ ] **Step 5: Run the full suite**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS — 688 tests, 0 failures.

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Dast tests/Naudit.Tests
git commit -m "test(dast): Fehlerpfade des App-Runners — Build kaputt, App kommt nicht hoch, Engine weg, Teardown garantiert"
```

---

### Task 6: Orphan sweeper + DI wiring

**Files:**
- Create: `src/Naudit.Infrastructure/Dast/DastOrphanSweeper.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs:95-115` (the session-sandbox block) and the SAST region
- Modify: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`
- Test: `tests/Naudit.Tests/DastOrphanSweeperTests.cs` (new), `tests/Naudit.Tests/DastWiringTests.cs` (new)

**Interfaces:**
- Consumes: `DockerAppRunner.NamePrefix`, `IDockerClient.ListContainersAsync/ListNetworksAsync/ListImagesAsync`.
- Produces: `DastOrphanSweeper : IHostedService`; DI registrations for `DastOptions`, `IAppRunner`, the sweeper, and one shared `IDockerClient`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Naudit.Tests/DastOrphanSweeperTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Dast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class DastOrphanSweeperTests
{
    /// <summary>Stürzt Naudit mitten in einem DAST-Lauf ab, bleiben Container/Netz/Image stehen —
    /// beim nächsten Start müssen sie weg sein (fremde Container bleiben unangetastet).</summary>
    [Fact]
    public async Task Start_removesLeftoverDastResources_andLeavesForeignOnesAlone()
    {
        var docker = new FakeDockerClient
        {
            Containers =
            {
                ["naudit-dast-app-abc123"] = true, ["naudit-dast-pw-abc123"] = true,
                ["naudit-session-7"] = true, ["postgres"] = true,
            },
        };
        await docker.CreateNetworkAsync("naudit-dast-net-abc123");
        await docker.CreateNetworkAsync("bridge");
        docker.Images.Add("naudit-dast-img-abc123");
        docker.Images.Add("mcr.microsoft.com/playwright/mcp:latest");   // ProbeImage-Cache: bleibt
        var sweeper = new DastOrphanSweeper(docker, NullLogger<DastOrphanSweeper>.Instance);

        await sweeper.StartAsync(CancellationToken.None);

        Assert.Equal(["naudit-session-7", "postgres"], docker.Containers.Keys.Order());
        Assert.Equal(["bridge"], docker.Networks.ToList());
        Assert.Equal(["mcr.microsoft.com/playwright/mcp:latest"], docker.Images);
    }

    [Fact]
    public async Task Start_dockerUnavailable_doesNotThrow()
    {
        var sweeper = new DastOrphanSweeper(new ThrowingDocker(), NullLogger<DastOrphanSweeper>.Instance);

        await sweeper.StartAsync(CancellationToken.None); // fail-quiet: Host startet trotzdem
    }

    private sealed class ThrowingDocker : FakeDockerClient
    {
        public override Task<IReadOnlyList<ContainerListEntry>> ListContainersAsync(
            string namePrefix, CancellationToken ct = default)
            => throw new Naudit.Infrastructure.Docker.DockerUnavailableException("fake: engine down");
    }
}
```

(`ListContainersAsync` in `FakeDockerClient` must become `public virtual` for the second test; `ContainerListEntry` comes from `Naudit.Infrastructure.Docker` — add the using.)

Create `tests/Naudit.Tests/DastWiringTests.cs` (same composition pattern as `SandboxWiringTests`):

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Dast;
using Xunit;

namespace Naudit.Tests;

public class DastWiringTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddNauditDatabase(config);
        services.AddNauditInfrastructure(config);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Naudit:Git:Platform"] = "GitLab",
        ["Naudit:GitLab:BaseUrl"] = "https://gitlab.example.com",
    };

    [Fact]
    public void Dast_disabledByDefault_registersNoAppRunner()
    {
        using var provider = Build(BaseSettings());

        Assert.Null(provider.GetService<IAppRunner>());
    }

    [Fact]
    public void Dast_enabled_registersAppRunner_andOrphanSweeper()
    {
        var settings = BaseSettings();
        settings["Naudit:Review:Dast:Enabled"] = "true";
        using var provider = Build(settings);

        Assert.NotNull(provider.GetService<IAppRunner>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is DastOrphanSweeper);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "DastOrphanSweeperTests|DastWiringTests"`
Expected: FAIL — `DastOrphanSweeper` does not exist; `IAppRunner` is never registered.

- [ ] **Step 3: Write the sweeper**

Create `src/Naudit.Infrastructure/Dast/DastOrphanSweeper.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Docker;

namespace Naudit.Infrastructure.Dast;

/// <summary>Räumt beim Start liegengebliebene DAST-Ressourcen ab (naudit-dast-*): nach einem
/// Absturz mitten im Lauf läuft sonst fremder PR-Code weiter. Nur Präfix-Treffer — fremde
/// Container/Netze/Images bleiben unangetastet. Fail-quiet: der Host startet auch ohne Docker.</summary>
public sealed class DastOrphanSweeper(IDockerClient docker, ILogger<DastOrphanSweeper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            foreach (var container in await docker.ListContainersAsync(DockerAppRunner.NamePrefix, ct))
            {
                logger.LogInformation("DAST: entferne verwaisten Container {Name}.", container.Name);
                await docker.RemoveContainerAsync(container.Name, ct);
            }
            foreach (var network in await docker.ListNetworksAsync(DockerAppRunner.NamePrefix, ct))
                await docker.RemoveNetworkAsync(network, ct);
            foreach (var image in await docker.ListImagesAsync(DockerAppRunner.NamePrefix, ct))
                await docker.RemoveImageAsync(image, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DAST: Aufräumen verwaister Ressourcen fehlgeschlagen.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

- [ ] **Step 4: Wire it up in DI**

In `src/Naudit.Infrastructure/DependencyInjection.cs`, **before** the session-sandbox block (it now shares the Docker client), add:

```csharp
        // DAST (dynamische Prüfung an der laufenden App): eigener Kill-Switch, bewusst unabhängig
        // von der Session-Sandbox — andere Risikoklasse (fremder PR-Code statt eigener Abo-Container).
        var dastOptions = configuration.GetSection("Naudit:Review:Dast").Get<DastOptions>() ?? new DastOptions();
        services.AddSingleton(dastOptions);
```

Replace the `IDockerClient` registration inside the sandbox branch with one shared registration in front of the branch:

```csharp
        // Ein Docker-Client für beide Nutzer (Session-Sandbox und DAST); ist die Sandbox aktiv,
        // gewinnt ihr Socket-Pfad.
        if (aiOptions.SessionSandbox == SessionSandbox.Docker || dastOptions.Enabled)
        {
            var socketPath = aiOptions.SessionSandbox == SessionSandbox.Docker
                ? sandboxOptions.DockerSocketPath
                : dastOptions.DockerSocketPath;
            services.AddSingleton<IDockerClient>(_ => new SocketDockerClient(socketPath));
        }
```

(delete the `services.AddSingleton<IDockerClient>(...)` line that currently sits inside `if (aiOptions.SessionSandbox == SessionSandbox.Docker)`).

Then, after the sandbox block:

```csharp
        if (dastOptions.Enabled)
        {
            // Kein HttpClient: der Healthcheck läuft als docker exec im Probe-Container —
            // Naudit spricht nie selbst HTTP mit der getesteten App.
            services.AddSingleton<IAppRunner>(sp => new DockerAppRunner(
                sp.GetRequiredService<IDockerClient>(),
                dastOptions,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<DockerAppRunner>()));
            services.AddHostedService<DastOrphanSweeper>();
        }
```

Add `using Naudit.Infrastructure.Dast;` to the file's usings.

- [ ] **Step 5: Add the config keys to the catalog**

In `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`, after the `Naudit:Review:Mcp:*` entries:

```csharp
        new("Naudit:Review:Dast:Enabled", false),
        new("Naudit:Review:Dast:DockerfilePath", false),
        new("Naudit:Review:Dast:AppPort", false),
        new("Naudit:Review:Dast:HealthPath", false),
        new("Naudit:Review:Dast:TimeBudget", false),
        new("Naudit:Review:Dast:MemoryLimitMb", false),
        new("Naudit:Review:Dast:CpuLimit", false),
        new("Naudit:Review:Dast:PidsLimit", false),
        new("Naudit:Review:Dast:MaxContextMb", false),
        new("Naudit:Review:Dast:DockerSocketPath", false),
        new("Naudit:Review:Dast:ProbeImage", false),
```

(`Projects` is deliberately absent — list-shaped keys stay env-only, like `ProjectTokens`.)

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "DastOrphanSweeperTests|DastWiringTests"`
Expected: PASS (4 tests).

- [ ] **Step 7: Run the full suite**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS — 692 tests, 0 failures (the session-sandbox wiring tests must still pass with the moved `IDockerClient` registration).

- [ ] **Step 8: Commit**

```bash
git add src/Naudit.Infrastructure tests/Naudit.Tests
git commit -m "feat(dast): Orphan-Sweeper + DI-Verdrahtung (geteilter IDockerClient, Settings-Katalog)"
```

---

### Task 7: Documentation

**Files:**
- Modify: `docs/dast.md` (an ops-oriented version already exists on main — status banner, Coolify part-A/part-B guide — from the `docs/docker-socket-dast` branch; merge main first)
- Modify: `docs/deployment.md`, `CLAUDE.md`

- [ ] **Step 1: Rewrite `docs/dast.md` into the feature reference**

Drop the "not yet released" banner, keep the ops guide (part A stays, part B updated to the final keys incl. `ProbeImage`), keep linking `docs/docker-socket.md` for everything socket-related, and make sure these sections exist (English, like the other docs):

1. **What it is** — Naudit builds the PR's own `Dockerfile`, runs it, and (from PR 2 on) probes it; PR 1 ships the runner only, nothing calls it yet.
2. **Two switches** — `Naudit:Review:Dast:Enabled` **and** `Naudit:Review:Dast:Projects` (empty = no project). State plainly: this builds and executes code from a pull request, so it belongs on repositories you trust; do not enable it with `AccessGate:Mode=Open`.
3. **Topology** — the diagram from the spec (all reachability runs through the Docker socket):

```text
Docker network  naudit-dast-net-<key>   (internal: true → no egress)
 ├─ app container    naudit-dast-app-<key>   (built from the PR's Dockerfile)
 └─ probe container  naudit-dast-pw-<key>    (ProbeImage; healthcheck via docker exec — PR 2 turns it into the Playwright-MCP server)
No published ports anywhere and Naudit never joins the network — every interaction is a
`docker exec` through the socket, so the app is unreachable from the host and the internet.
```

4. **Config table** — every key from `DastOptions` with defaults and meaning.
5. **Isolation** — internal network (no egress), memory/CPU/PID limits, `--cap-drop ALL`, `no-new-privileges`, no volume, no environment, no Docker socket in the app container.
6. **Fail-open table** — not allow-listed / no Dockerfile / context over `MaxContextMb` / build failed / never healthy / socket gone / budget exceeded ⇒ each logs and yields "no dynamic grounding", never a failed review.
7. **Lifecycle & teardown** — guaranteed teardown via `IAsyncDisposable`, plus the orphan sweeper at startup.
8. **Requirement** — a reachable Docker engine socket on the same host (Linux only: the client speaks Unix sockets, the `win-x64` binary is out). It does **not** matter whether Naudit itself runs containerized (socket mounted + `group_add`) or as a bare process (user in the `docker` group) — all interaction with the review network runs through the socket, so both deployments behave identically. Same `docker.sock` trust note as `docs/session-sandbox.md` (link it rather than repeating it).

- [ ] **Step 2: Extend `docs/deployment.md`**

In the section that documents the Docker socket mount for the session sandbox, add that DAST uses the same mount and the same `group_add` GID, but is switched on separately (`Naudit:Review:Dast:Enabled` + `Projects`), and link `docs/dast.md`.

- [ ] **Step 3: Extend `CLAUDE.md`**

Add a bullet to "Extension points" after the session-sandbox bullet:

```markdown
- **DAST app-runner (PR 1 of the dynamic-testing slice):** `IAppRunner`/`DockerAppRunner`
  (`src/Naudit.Infrastructure/Dast/`) builds the PR's own `Dockerfile` (checkout tar'd into the
  engine via `WorkspaceTarPacker` — the daemon cannot see Naudit's filesystem), starts it as a
  sibling container on a per-review `internal` network (no egress, no published ports, memory/CPU/PID
  limits, `cap-drop ALL`, no volume, no environment) next to a passive **probe container**
  (`ProbeImage`, pulled on demand, `sleep`-entrypoint). All reachability runs through the Docker
  socket: the healthcheck is a `docker exec` in the probe container (PR 2 speaks MCP over exec-stdio
  through the same container), Naudit never joins the network, and the runner works identically from
  a containerized or bare-metal Naudit. Returns a `RunningApp` whose `DisposeAsync` tears both
  containers, network and built image down. Gated twice:
  `Naudit:Review:Dast:Enabled` **and** the `Naudit:Review:Dast:Projects` allowlist (empty ⇒ no
  project) — it executes foreign PR code. Fail-open everywhere (`null`, never a throw), plus a
  `DastOrphanSweeper` that removes `naudit-dast-*` leftovers at startup. `IReviewWorkspace` gained
  `ProjectId` for that allowlist. Nothing calls the runner yet — the `DastAnalyzer : ISastAnalyzer`
  and the Playwright probing arrive in PR 2. See `docs/dast.md`.
```

- [ ] **Step 4: Verify the docs build nothing but still check the suite**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS — 692 tests, 0 failures.

- [ ] **Step 5: Commit**

```bash
git add docs CLAUDE.md
git commit -m "docs(dast): App-Runner dokumentiert (Topologie, doppelte Freigabe, Isolation, Fail-Open)"
```

---

## Manual verification gate (before PR 2 is worth starting)

CI never touches real Docker, so run this once by hand on a machine with a Docker socket. Because all reachability runs through the socket, the run works from a containerized Naudit (socket mounted + `group_add`) **and** from a bare `dotnet run` (user in the `docker` group) — if bare-metal operation matters to you, repeat step 2 once in each mode; behaviour must be identical:

1. `NAUDIT_TEST_DOCKER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SocketDockerClientTests` — engine round-trip incl. the new network lifecycle.
2. Point `Naudit:Review:Dast:Projects` at a small web repo with a `Dockerfile`, call `IAppRunner.RunAsync` from a scratch endpoint or a debug harness, and confirm: image built, app **and** probe container on `naudit-dast-net-*`, healthcheck green (exec probe in the pw container), `docker inspect` shows the limits and `CapDrop: [ALL]` on both, `curl` from the **host** to the app fails (no published port anywhere), `docker exec` into the app container has no internet, and after `DisposeAsync` no `naudit-dast-*` container/network/image remains (the pulled `ProbeImage` stays — it is cache).
3. Kill Naudit mid-run (`docker kill` / `kill -9`) and restart it — the orphan sweeper must clear the leftovers, including the probe container.

## Out of scope (PR 2)

`DastAnalyzer : ISastAnalyzer`, `FindingCategory.Dast`, turning the probe container into the Playwright-MCP server (its real entrypoint spoken to over **stdio via `docker exec`** — the MCP connector from #54 needs an exec transport for this, reusing the session sandbox's `DockerStreamDemux` pattern; no published MCP port), the probing prompt/tool-loop, `MaxProbeSteps`, and the mapping of raw probe results to `ScanFinding`. The probing LLM call will use the **global** `IChatClient` (never the author-session router), matching `DistillingReviewGuidelines`.
