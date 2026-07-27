# DAST PR 2 — Probing-Analyzer (Playwright-MCP über exec-stdio) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `DastAnalyzer : ISastAnalyzer` that boots the PR's app via the PR-1 app-runner, drives a Playwright-MCP server (running as a `docker exec` stdio process inside the probe container) through an agentic LLM tool-loop, and feeds the resulting security observations into the review prompt as `FindingCategory.Dast` grounding — fail-open, verdict stays LLM-driven.

**Architecture:** All MCP reachability stays on the Docker socket (decision 2026-07-24): a new **bidirectional attached exec** (`ExecStreamAsync`) on `SocketDockerClient` hands the SDK's `StreamClientTransport(serverInput, serverOutput, loggerFactory)` a raw duplex stream pair — no `docker` CLI in the image, no new NuGet. `DastProbeSession` owns that stream + `McpClient` for one review and disposes both. `DastAnalyzer` wraps the **global** `IChatClient` in a locally-bounded `UseFunctionInvocation` (capped by `DastOptions.MaxProbeSteps`), runs a probing system prompt with the Playwright tools, parses the model's JSON observations into `ScanFinding`s, and guarantees teardown of the whole DAST topology via the PR-1 `RunningApp`.

**Tech Stack:** .NET 10, `ModelContextProtocol.Core` 1.4.1 (`StreamClientTransport` — already referenced, no version bump), hand-rolled Docker Engine-API duplex exec over the Unix socket (no Docker.DotNet), xUnit with `FakeDockerClient`/`FakeChatClient`; real-engine round-trips are opt-in integration tests gated on `NAUDIT_TEST_DOCKER=1`.

**Spec:** `docs/superpowers/specs/2026-07-19-dast-design.md` (authoritative; PR 2 = "Probing-Analyzer"). This is **PR 2 of 2**; PR 1 (app-runner, `IAppRunner`/`DockerAppRunner`/`DastOrphanSweeper`, network/build/image Docker ops, `DastOptions` allowlist) is **merged on main**. Branch: `feat/dast-probing` (off main `94ecb39`).

## Global Constraints

- Solution file is `Naudit.slnx` — `dotnet build Naudit.slnx`. Single class: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter <Name>`.
- **Run the full suite with `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`.** Baseline on this branch: **700/700 green**.
- **Core rule:** `Naudit.Core` depends only on `Microsoft.Extensions.AI.Abstractions`. This PR's Core change is exactly one enum value — `FindingCategory.Dast` — appended last so existing ordinals are stable. No MCP/Docker types in Core. `DastAnalyzer` lives in Infrastructure and implements the existing Core `ISastAnalyzer`.
- **No real Docker in CI.** All analyzer/session/connector unit tests go through `FakeDockerClient` + a fake MCP surface; engine-API round-trips (the duplex exec) are opt-in `NAUDIT_TEST_DOCKER=1` integration tests (pattern: `SocketDockerClientTests`).
- **Fail-open everywhere:** not allow-listed / no Dockerfile / app never healthy / probe image or MCP server won't start / exec stream dies / LLM loop throws / non-JSON output ⇒ logged warning + **empty findings**, never an exception at `AnalyzeAsync`'s caller. The only exception that may leave `AnalyzeAsync` is a **caller** cancellation (`ct`). The probing step is grounding, **not** the fail-closed review final-turn — non-JSON ⇒ "no findings", never a thrown review.
- **Verdict stays LLM-driven.** DAST findings are prompt grounding only (like Semgrep/Trivy). The severity-aware gate never reads `ScanFinding`s (`ReviewService` gate reads the LLM's own per-comment severity/confidence — unchanged).
- **No Naudit secrets in the app/probe containers** (unchanged from PR 1). The probing LLM call uses the **global** `IChatClient` (never the author-session router), matching `DistillingReviewGuidelines`.
- Naming: DAST resources stay `naudit-dast-*` (PR 1). The pulled `ProbeImage` is never swept.
- Code comments in German, docs in English. TDD: red → green → one commit per task.
- Config: scalar `Naudit:Review:Dast:MaxProbeSteps` joins `SettingsCatalog` (non-secret); the `Projects` allowlist stays env-only (PR 1 precedent).

## Verified facts this plan is built on

- **SDK transport:** `ModelContextProtocol.Protocol.StreamClientTransport(Stream serverInput, Stream serverOutput, ILoggerFactory loggerFactory)` implements `ModelContextProtocol.Client.IClientTransport`; `McpClient.CreateAsync(IClientTransport, McpClientOptions?, ILoggerFactory?, CancellationToken)` accepts it (same call site shape as `McpClientToolConnector.cs`). `serverInput` = stream we **write** (MCP server stdin), `serverOutput` = stream we **read** (MCP server stdout).
- **Docker duplex exec:** `SocketDockerClient` uses `HttpClient` over a `SocketsHttpHandler.ConnectCallback` that opens a `NetworkStream` on a `UnixDomainSocketEndPoint`. `HttpClient` cannot reclaim a request-side stream mid-flight, so the attached exec needs a **new low-level path**: open the raw socket the same way `ConnectCallback` does, write the HTTP/1.1 request line + headers by hand, then use the resulting `NetworkStream` as the duplex channel. Read side is **multiplexed** (`DockerStreamDemux` 8-byte frame header: `[0]`=stream type 1=stdout/2=stderr, `[4..7]`=big-endian uint32 length); **write side (stdin) is raw/unframed**.
- **Probe launch:** `mcr.microsoft.com/playwright/mcp:latest`, entrypoint `node /app/cli.js --headless --browser chromium --no-sandbox`, user `node` (uid 1000), Node 22. No `--port` ⇒ stdio MCP. PR-1 probe container runs `sleep infinity`; PR 2 launches the server via exec argv `["node","/app/cli.js","--headless","--browser","chromium","--no-sandbox"]`.
- **E2E gate (2026-07-25) passed** on real engine 29.5.3. Operational caveat to document: an app image that starts as root and drops privileges (stock nginx) fails under `CapDrop:[ALL]` — that is the runner working as designed.
- `ReviewService` runs `IEnumerable<ISastAnalyzer>` whenever `_analyzers.Count > 0` (not gated on `sastOptions.Enabled`), and a checkout happens when analyzers are present — so registering `DastAnalyzer` as an `ISastAnalyzer` in the `dastOptions.Enabled` DI block makes it run and receive a workspace, even with SAST off.

## File Structure

**New (`src/Naudit.Infrastructure/Dast/`)**

| File | Responsibility |
| --- | --- |
| `DastProbeSession.cs` | Owns one review's MCP client: launches the exec-stdio MCP server in the probe container, builds `StreamClientTransport` + `McpClient`, lists tools, `IAsyncDisposable` tears both down. |
| `DastAnalyzer.cs` | `ISastAnalyzer`: allowlist gate → `IAppRunner.RunAsync` → `DastProbeSession` → bounded agentic tool-loop on the global `IChatClient` → JSON → `ScanFinding[]`; guaranteed teardown; fail-open. |
| `DastProbePrompt.cs` | The probing system prompt constant + the strict JSON response contract the loop must return. |

**New (`src/Naudit.Infrastructure/Docker/`)**

| File | Responsibility |
| --- | --- |
| `DockerExecStream.cs` | The duplex handle returned by `ExecStreamAsync`: a writable `StdinStream` (raw) + a readable `StdoutStream` (incrementally demuxed), plus `DisposeAsync` closing the socket. |

**Modified**

| File | Change |
| --- | --- |
| `src/Naudit.Core/Models/ScanFinding.cs` | `FindingCategory` gains `Dast` (appended). |
| `src/Naudit.Core/Review/PromtBuilder.cs` | `AppendCategory` for the `Dast` group. |
| `src/Naudit.Infrastructure/Docker/IDockerClient.cs` | `ExecStreamAsync(name, argv, env, workingDir, ct) -> Task<DockerExecStream>`. |
| `src/Naudit.Infrastructure/Docker/SocketDockerClient.cs` | Hand-rolled attached-exec connect + framing. |
| `src/Naudit.Infrastructure/Docker/DockerStreamDemux.cs` | Add an incremental single-frame reader used by the streaming read side. |
| `src/Naudit.Infrastructure/Dast/DastOptions.cs` | `MaxProbeSteps` (default 12), `ProbeMcpArgv` (the exec argv). |
| `src/Naudit.Infrastructure/DependencyInjection.cs` | Register `IAppRunner` consumer: `ISastAnalyzer`=`DastAnalyzer` + its deps when `dastOptions.Enabled`. |
| `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs` | `Naudit:Review:Dast:MaxProbeSteps`. |
| `tests/Naudit.Tests/Fakes/FakeDockerClient.cs` | `ExecStreamAsync` returning a scriptable in-memory duplex. |
| `docs/dast.md`, `CLAUDE.md` | Probing documented; root-drop caveat; `MaxProbeSteps`. |

---

### Task 1: `FindingCategory.Dast` + prompt rendering

**Files:**
- Modify: `src/Naudit.Core/Models/ScanFinding.cs:4`
- Modify: `src/Naudit.Core/Review/PromtBuilder.cs:182-184`
- Test: `tests/Naudit.Tests/PromtBuilderTests.cs`

**Interfaces:**
- Produces: `FindingCategory.Dast` (enum member, last); a "DAST (dynamic)" grounding section rendered by `PromptBuilder.Build` when any finding has `Category == FindingCategory.Dast`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Naudit.Tests/PromtBuilderTests.cs` (match the file's existing construction of `PromptBuilder.Build` and its `ScanFinding` args — read the neighbouring tests first; the finding list parameter is the redacted `IReadOnlyList<ScanFinding>`):

```csharp
    [Fact]
    public void Build_rendersDastFindings_underDynamicHeading()
    {
        var findings = new List<ScanFinding>
        {
            new("dast", FindingCategory.Dast, FindingSeverity.High,
                "Reflected XSS at /search?q= — payload echoed unescaped", RuleId: null, FilePath: null, Line: null),
        };

        var messages = PromptBuilder.Build(SystemPrompt, Request, Changes, findings);

        var user = string.Join("\n", messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        Assert.Contains("DAST (dynamic)", user);
        Assert.Contains("Reflected XSS at /search", user);
    }
```

(Use the same `SystemPrompt`/`Request`/`Changes` helpers the other tests in the file use. If `Build`'s findings parameter is optional/positional, mirror an existing findings-based test exactly.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~Build_rendersDastFindings"`
Expected: FAIL — either `FindingCategory.Dast` does not compile (CS0117) or the heading is absent.

- [ ] **Step 3: Add the enum member**

In `src/Naudit.Core/Models/ScanFinding.cs:4`:

```csharp
public enum FindingCategory { Sast, Sca, Secrets, Dast }
```

- [ ] **Step 4: Render the group**

In `src/Naudit.Core/Review/PromtBuilder.cs`, directly after the SAST line (currently line 184):

```csharp
        AppendCategory(sb, "DAST (dynamic)", findings.Where(f => f.Category == FindingCategory.Dast));
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PromtBuilderTests`
Expected: PASS (existing + the new test).

- [ ] **Step 6: Build + full suite**

Run: `dotnet build Naudit.slnx && DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS — 701 (700 + 1).

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Core/Models/ScanFinding.cs src/Naudit.Core/Review/PromtBuilder.cs tests/Naudit.Tests/PromtBuilderTests.cs
git commit -m "feat(dast): FindingCategory.Dast + Prompt-Sektion für dynamische Funde"
```

---

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

### Task 3: Bidirectional attached exec on the Docker seam

This is the load-bearing, highest-risk task: a hand-rolled duplex exec over the Unix socket. Its true gate is the **`NAUDIT_TEST_DOCKER=1` integration test** against a real engine; the fake-based unit tests pin the seam shape. Iterate the implementation against the real-Docker test — never weaken the test.

**Files:**
- Create: `src/Naudit.Infrastructure/Docker/DockerExecStream.cs`
- Modify: `src/Naudit.Infrastructure/Docker/IDockerClient.cs`, `src/Naudit.Infrastructure/Docker/SocketDockerClient.cs`, `src/Naudit.Infrastructure/Docker/DockerStreamDemux.cs`
- Modify: `tests/Naudit.Tests/Fakes/FakeDockerClient.cs`
- Test: `tests/Naudit.Tests/SocketDockerClientTests.cs` (new gated method)

**Interfaces:**
- Consumes: the existing `SocketDockerClient` socket-connect logic (the `ConnectCallback`/`UnixDomainSocketEndPoint` path) and `DockerStreamDemux` frame format.
- Produces: `IDockerClient.ExecStreamAsync(string name, IReadOnlyList<string> argv, IReadOnlyDictionary<string,string?>? environment, string workingDirectory, CancellationToken ct = default) -> Task<DockerExecStream>`; `DockerExecStream : IAsyncDisposable` exposing `Stream Stdin` (write, raw) and `Stream Stdout` (read, demuxed to stdout bytes only); `DockerStreamDemux.ReadFrameAsync(Stream source, CancellationToken) -> (byte StreamType, byte[] Payload)?` (null at EOF).

- [ ] **Step 1: Write the failing gated integration test**

Append to `tests/Naudit.Tests/SocketDockerClientTests.cs` (reuse the file's `Enabled`/`SocketPath`/`Image` members; adapt names to what exists):

```csharp
    /// <summary>Bidirektionaler exec gegen echtes Docker: in einem laufenden Container `cat` starten,
    /// über stdin schreiben, demuxten stdout zurücklesen — die Naht, auf der die DAST-MCP-Brücke sitzt.</summary>
    [Fact]
    public async Task ExecStream_roundtripsStdinToStdout()
    {
        if (!Enabled) return; // ohne NAUDIT_TEST_DOCKER: übersprungen

        using var docker = new SocketDockerClient(SocketPath);
        var name = $"naudit-dast-pw-{Guid.NewGuid():N}";
        try
        {
            await docker.RunDetachedAsync(new ContainerRunSpec(name, Image, VolumeName: null, VolumeTarget: null,
                Command: []) { Entrypoint = ["sleep", "infinity"] });

            await using var exec = await docker.ExecStreamAsync(name, ["cat"], environment: null, workingDirectory: "/");
            var payload = System.Text.Encoding.UTF8.GetBytes("naudit-dast-probe\n");
            await exec.Stdin.WriteAsync(payload);
            await exec.Stdin.FlushAsync();

            var buf = new byte[payload.Length];
            var read = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (read < buf.Length)
            {
                var n = await exec.Stdout.ReadAsync(buf.AsMemory(read), cts.Token);
                if (n == 0) break;
                read += n;
            }
            Assert.Equal("naudit-dast-probe\n", System.Text.Encoding.UTF8.GetString(buf, 0, read));
        }
        finally
        {
            await docker.RemoveContainerAsync(name);
        }
    }
```

- [ ] **Step 2: Write the fake-based unit test (runs in CI)**

Append to `tests/Naudit.Tests/SocketDockerClientTests.cs` a fake-independent test living wherever `FakeDockerClient` is exercised — but the shape check belongs with the fake. Add to a new `tests/Naudit.Tests/FakeDockerExecStreamTests.cs`:

```csharp
using System.Text;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class FakeDockerExecStreamTests
{
    [Fact]
    public async Task ExecStream_fake_echoesScriptedStdout_andRecordsArgv()
    {
        var docker = new FakeDockerClient();
        docker.NextExecStdout = Encoding.UTF8.GetBytes("hello-from-probe");

        await using var exec = await docker.ExecStreamAsync("naudit-dast-pw-1", ["node", "/app/cli.js"],
            environment: null, workingDirectory: "/");
        await exec.Stdin.WriteAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}"));

        var buf = new byte[64];
        var n = await exec.Stdout.ReadAsync(buf);

        Assert.Equal("hello-from-probe", Encoding.UTF8.GetString(buf, 0, n));
        Assert.Contains(docker.ExecStreamCalls, c => c.Container == "naudit-dast-pw-1" && c.Argv[0] == "node");
        Assert.Contains("{\"jsonrpc\":\"2.0\"}", Encoding.UTF8.GetString(docker.LastExecStdin!));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet build Naudit.slnx`
Expected: FAIL — `ExecStreamAsync`/`DockerExecStream` do not exist (CS1061/CS0246). The compile failure is the red signal (the gated test returns early in CI).

- [ ] **Step 4: The duplex handle**

Create `src/Naudit.Infrastructure/Docker/DockerExecStream.cs`:

```csharp
namespace Naudit.Infrastructure.Docker;

/// <summary>Duplex-Kanal eines attached `docker exec`: Stdin (roh geschrieben) + Stdout (aus dem
/// gemultiplexten Docker-Stream heraus-demuxt). DisposeAsync schließt die zugrunde liegende
/// Socket-Verbindung. Für den MCP-Transport: Stdin = serverInput, Stdout = serverOutput.</summary>
public sealed class DockerExecStream(Stream stdin, Stream stdout, IAsyncDisposable underlying) : IAsyncDisposable
{
    public Stream Stdin { get; } = stdin;
    public Stream Stdout { get; } = stdout;

    public async ValueTask DisposeAsync() => await underlying.DisposeAsync();
}
```

- [ ] **Step 5: Incremental demux frame reader**

Add to `src/Naudit.Infrastructure/Docker/DockerStreamDemux.cs` (keep the existing `ReadAsync`):

```csharp
    /// <summary>Liest genau EINEN Frame (8-Byte-Header: [0]=Stream-Typ 1=stdout/2=stderr,
    /// [4..7]=Big-Endian-Länge, dann Payload). Null bei EOF. Für den inkrementellen (Streaming-)
    /// Lesepfad, im Gegensatz zum bestehenden ReadAsync, das bis zum Ende puffert.</summary>
    public static async Task<(byte StreamType, byte[] Payload)?> ReadFrameAsync(Stream source, CancellationToken ct)
    {
        var header = new byte[8];
        if (!await ReadExactlyOrEofAsync(source, header, ct))
            return null;
        var length = (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
        var payload = new byte[length];
        if (length > 0 && !await ReadExactlyOrEofAsync(source, payload, ct))
            return null;
        return (header[0], payload);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await source.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
```

- [ ] **Step 6: The attached-exec connect on `SocketDockerClient`**

First add to `IDockerClient.cs` (below `ExecAsync`):

```csharp
    /// <summary>Wie ExecAsync, aber attached und bidirektional: AttachStdin=true, non-TTY (gemultiplext).
    /// Liefert einen Duplex-Kanal (Stdin roh, Stdout demuxt) für den stdio-MCP-Transport. Transport-/
    /// API-Fehler werfen DockerUnavailableException; der Aufrufer behandelt das fail-open.</summary>
    Task<DockerExecStream> ExecStreamAsync(string name, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string?>? environment, string workingDirectory, CancellationToken ct = default);
```

Implement in `SocketDockerClient.cs`. This bypasses `HttpClient` for the *start* call because the connection must stay duplex; it reuses the same Unix-socket connect the handler's `ConnectCallback` uses (extract that connect into a private helper `ConnectRawAsync()` if it is currently an inline lambda — a `Task<Stream>` opening the `UnixDomainSocketEndPoint` and returning the `NetworkStream`):

```csharp
    public async Task<DockerExecStream> ExecStreamAsync(string name, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string?>? environment, string workingDirectory, CancellationToken ct = default)
    {
        // 1) exec create über den normalen HTTP-Weg (buffered) — liefert die Exec-Id.
        var envArr = environment?.Select(kv => $"{kv.Key}={kv.Value}").ToArray();
        var createBody = new Dictionary<string, object?>
        {
            ["AttachStdin"] = true, ["AttachStdout"] = true, ["AttachStderr"] = true, ["Tty"] = false,
            ["Cmd"] = argv, ["WorkingDir"] = workingDirectory,
        };
        if (envArr is { Length: > 0 }) createBody["Env"] = envArr;
        using var createResp = await SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"/containers/{Uri.EscapeDataString(name)}/exec")
        { Content = JsonContent.Create(createBody, options: OutJsonOpts) }, ct);
        await EnsureAsync(createResp, ct);
        var execId = (await ReadJsonAsync<ExecCreateResponse>(createResp, ct)).Id
            ?? throw new DockerUnavailableException("exec create ohne Id");

        // 2) exec start als roher, duplexer HTTP/1.1-Request direkt auf dem Socket — HttpClient kann
        //    die Schreibseite nicht zurückgeben, daher hand-geschriebene Request-Zeile + Header.
        Stream raw = await ConnectRawAsync(ct);
        try
        {
            var startJson = "{\"Detach\":false,\"Tty\":false}";
            var body = System.Text.Encoding.UTF8.GetBytes(startJson);
            var request =
                $"POST /exec/{execId}/start HTTP/1.1\r\n" +
                "Host: docker\r\n" +
                "Content-Type: application/json\r\n" +
                "Upgrade: tcp\r\nConnection: Upgrade\r\n" +
                $"Content-Length: {body.Length}\r\n\r\n";
            await raw.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request), ct);
            await raw.WriteAsync(body, ct);
            await raw.FlushAsync(ct);

            // Antwort-Header bis zur Leerzeile konsumieren; danach ist `raw` der duplexe Attach-Stream.
            await ConsumeHttpHeadersAsync(raw, ct);

            var stdout = new DemuxReadStream(raw);            // liest nur stdout-Frames heraus
            var underlying = new RawStreamDisposable(raw);
            return new DockerExecStream(stdin: raw, stdout: stdout, underlying);
        }
        catch
        {
            await raw.DisposeAsync();
            throw;
        }
    }
```

Add the supporting private types at the bottom of the file:

```csharp
    private sealed record ExecCreateResponse(string? Id);

    /// <summary>Liest bis zur \r\n\r\n-Grenze der HTTP-Antwort (Statuszeile + Header) und verwirft sie;
    /// danach folgt der rohe/gemultiplexte Attach-Body.</summary>
    private static async Task ConsumeHttpHeadersAsync(Stream s, CancellationToken ct)
    {
        var window = new byte[4];
        var one = new byte[1];
        var filled = 0;
        while (true)
        {
            if (await s.ReadAsync(one.AsMemory(0, 1), ct) == 0)
                throw new DockerUnavailableException("exec start: Verbindung vor den Headern geschlossen");
            window[filled % 4] = one[0];
            filled++;
            if (filled >= 4)
            {
                var i = filled % 4;
                if (window[(i + 0) % 4] == (byte)'\r' && window[(i + 1) % 4] == (byte)'\n' &&
                    window[(i + 2) % 4] == (byte)'\r' && window[(i + 3) % 4] == (byte)'\n')
                    return;
            }
        }
    }

    /// <summary>Lese-Stream, der aus dem gemultiplexten Docker-Attach-Body fortlaufend die
    /// stdout-Frames (Typ 1) demuxt und stderr (Typ 2) verwirft.</summary>
    private sealed class DemuxReadStream(Stream source) : Stream
    {
        private byte[] _pending = [];
        private int _offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_offset >= _pending.Length)
            {
                var frame = await DockerStreamDemux.ReadFrameAsync(source, ct);
                if (frame is null) return 0;                       // EOF
                if (frame.Value.StreamType == 2) continue;         // stderr verwerfen
                _pending = frame.Value.Payload; _offset = 0;
                if (_pending.Length == 0) continue;
            }
            var n = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return n;
        }

        public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private sealed class RawStreamDisposable(Stream raw) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await raw.DisposeAsync();
    }
```

> **Implementer note:** `ConnectRawAsync` must be the *same* Unix-socket connect the handler already uses. If the current code only has it as an inline `ConnectCallback` lambda, extract a `private static async ValueTask<Stream> ConnectRawAsync(string socketPath, CancellationToken ct)` (open `Socket(AddressFamily.Unix, Stream, IP=0)`, `ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct)`, return `new NetworkStream(socket, ownsSocket: true)`) and call it from both the handler callback and here. Keep the exec-create call on the existing buffered `_http` path (only `/exec/{id}/start` needs the raw duplex). If the real-Docker test shows the header-boundary scan or the upgrade handshake misbehaving (Docker may answer `101 UPGRADED` or `200 OK` depending on version), adjust `ConsumeHttpHeadersAsync` to simply read until the first `\r\n\r\n` regardless of status — that is already what it does; do not special-case the status line.

- [ ] **Step 7: Extend `FakeDockerClient`**

In `tests/Naudit.Tests/Fakes/FakeDockerClient.cs`:

```csharp
    public List<(string Container, IReadOnlyList<string> Argv)> ExecStreamCalls { get; } = new();
    public byte[]? NextExecStdout { get; set; }
    public byte[]? LastExecStdin { get; private set; }

    public Task<DockerExecStream> ExecStreamAsync(string name, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string?>? environment, string workingDirectory, CancellationToken ct = default)
    {
        ExecStreamCalls.Add((name, argv));
        var stdinCapture = new CapturingStream(b => LastExecStdin = b);
        var stdout = new MemoryStream(NextExecStdout ?? []);
        return Task.FromResult(new DockerExecStream(stdinCapture, stdout, new NoopAsyncDisposable()));
    }

    private sealed class CapturingStream(Action<byte[]> onWrite) : Stream
    {
        private readonly MemoryStream _buf = new();
        public override void Write(byte[] b, int o, int c) { _buf.Write(b, o, c); onWrite(_buf.ToArray()); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default)
        { _buf.Write(b.Span); onWrite(_buf.ToArray()); return ValueTask.CompletedTask; }
        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => _buf.Length;
        public override long Position { get => _buf.Position; set => _buf.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
```

(Add the `internal` no-op/subclass stubs for the two other `IDockerClient` doubles — `ThrowingDockerClient` in `AccountServiceTests.cs`/`ClaudeSessionServiceTests.cs` — as one-line `throw new NotSupportedException()` methods, mechanical, to keep the build green.)

- [ ] **Step 8: Build + fake unit test + full suite**

Run: `dotnet build Naudit.slnx && dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter FakeDockerExecStreamTests`
Expected: PASS (1). Then `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx` — 703 (701 + 2; the gated integration test returns early).

- [ ] **Step 9: Real-Docker validation (mandatory before Task 4)**

Run: `NAUDIT_TEST_DOCKER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SocketDockerClientTests`
Expected: PASS incl. `ExecStream_roundtripsStdinToStdout`. If it hangs or mismatches, fix the connect/demux code (never the test) until the round-trip is byte-exact. Record the output in the commit message body.

- [ ] **Step 10: Commit**

```bash
git add src/Naudit.Infrastructure/Docker tests/Naudit.Tests
git commit -m "feat(dast): bidirektionaler docker exec (Stdin roh, Stdout demuxt) für die stdio-MCP-Bruecke"
```

---

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

### Task 5: `DastAnalyzer` — orchestration happy path

**Files:**
- Create: `src/Naudit.Infrastructure/Dast/DastProbePrompt.cs`, `src/Naudit.Infrastructure/Dast/DastAnalyzer.cs`
- Test: `tests/Naudit.Tests/DastAnalyzerTests.cs`

**Interfaces:**
- Consumes: `IAppRunner.RunAsync` (PR 1), `DastProbeSession` (Task 4), `IChatClient` (global), `DastOptions`, `IReviewWorkspace`, `FakeChatClient`, `FakeDockerClient`, and a fake `IAppRunner`.
- Produces: `DastAnalyzer : ISastAnalyzer` (`Name => "dast"`, `AnalyzeAsync(workspace, changes, ct) -> IReadOnlyList<ScanFinding>`). Emits `ScanFinding(Tool: "dast", Category: FindingCategory.Dast, …)` with the endpoint in `Message`, `FilePath`/`Line` null.

- [ ] **Step 1: Write the probing prompt + JSON contract**

Create `src/Naudit.Infrastructure/Dast/DastProbePrompt.cs`:

```csharp
namespace Naudit.Infrastructure.Dast;

/// <summary>System-Prompt für den agentischen Probing-Lauf. „Playwright ist die Hand, nicht das Hirn":
/// der Browser navigiert, das LLM beurteilt. Grounding-Schritt ⇒ non-JSON ist „keine Funde", nie ein
/// fail-closed-Abbruch.</summary>
public static class DastProbePrompt
{
    public static string System(string appUrl, int maxSteps) =>
        $$"""
        You are a security probe driving a headless browser (Playwright tools) against a running
        web app at {{appUrl}}. Explore reachable pages and forms and look for evidence of concrete
        vulnerabilities: reflected/stored XSS, obvious injection, missing auth on sensitive routes,
        open redirects, sensitive data in responses. Use at most {{maxSteps}} tool calls; be frugal.

        You are grounding a code review, not producing a final verdict. When done, respond with ONLY
        a JSON object, no prose:
        {"findings":[{"severity":"High|Medium|Low","endpoint":"<url or route>","summary":"<one line>"}]}
        If you found nothing, respond exactly {"findings":[]}.
        """;
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/Naudit.Tests/DastAnalyzerTests.cs`. The `FakeChatClient` must return the JSON contract; a fake `IAppRunner` returns a `RunningApp`; the probe session is exercised via `FakeDockerClient` (no live MCP — so the analyzer must tolerate a probe-session start failure as fail-open, tested in Task 6; here we test the path where the model answers with findings). To make the happy path deterministic without a live MCP peer, inject the probe-session factory as a delegate so the test supplies tools directly:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Dast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class DastAnalyzerTests
{
    private const string Project = "acme/shop";

    private sealed class Ws(string root) : IReviewWorkspace
    {
        public string RootPath => root;
        public string ProjectId => Project;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DastOptions Options() => new() { Enabled = true, Projects = { Project } };

    [Fact]
    public async Task Analyze_mapsModelJson_toDastFindings()
    {
        var app = new FakeAppRunner(); // RunAsync -> non-null RunningApp
        var chat = new FakeChatClient(
            "{\"findings\":[{\"severity\":\"High\",\"endpoint\":\"/search?q=\",\"summary\":\"Reflected XSS\"}]}");
        var analyzer = new DastAnalyzer(app, Options(), chat, new FakeDockerClient(),
            NullLoggerFactory.Instance, probeToolsOverride: []);   // leere Toolliste ⇒ kein echter MCP

        var findings = await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        var f = Assert.Single(findings);
        Assert.Equal("dast", f.Tool);
        Assert.Equal(FindingCategory.Dast, f.Category);
        Assert.Equal(FindingSeverity.High, f.Severity);
        Assert.Contains("/search?q=", f.Message);
        Assert.Contains("Reflected XSS", f.Message);
        Assert.Null(f.FilePath);
        Assert.True(app.Disposed);   // Teardown lief
    }

    [Fact]
    public async Task Analyze_notAllowlisted_returnsEmpty_withoutRunning()
    {
        var app = new FakeAppRunner();
        var opts = Options(); opts.Projects.Clear();
        var analyzer = new DastAnalyzer(app, opts, new FakeChatClient("{\"findings\":[]}"),
            new FakeDockerClient(), NullLoggerFactory.Instance, probeToolsOverride: []);

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
        Assert.False(app.RunCalled);
    }
}
```

Add the `FakeAppRunner` to `tests/Naudit.Tests/Fakes/` (implements `IAppRunner`; `RunAsync` records `RunCalled`, returns a `RunningApp("http://naudit-dast-app-x:8080/", "naudit-dast-net-x", "naudit-dast-app-x", "naudit-dast-pw-x", () => { Disposed = true; return ValueTask.CompletedTask; })` unless configured to return null). Confirm `FakeChatClient` can be constructed with a fixed response string returning it from `GetResponseAsync` — if the existing fake differs, adapt to its constructor.

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DastAnalyzerTests`
Expected: FAIL — `DastAnalyzer` does not exist (CS0246).

- [ ] **Step 4: Write the analyzer**

Create `src/Naudit.Infrastructure/Dast/DastAnalyzer.cs`. It wraps the global `IChatClient` in a locally-bounded `UseFunctionInvocation` (cap `MaxProbeSteps`), passes the probe tools in `ChatOptions.Tools`, and parses the JSON. The `probeToolsOverride` seam lets tests bypass the live MCP; production passes `null` and the analyzer opens a real `DastProbeSession`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Docker;

namespace Naudit.Infrastructure.Dast;

/// <summary>Dynamische Prüfung als weiterer ISastAnalyzer: baut/startet die PR-App (PR-1-Runner),
/// treibt den Playwright-MCP-Server durch einen begrenzten agentischen Loop und mappt die
/// JSON-Beobachtungen des Modells auf ScanFinding(Category=Dast). Reines Grounding, Verdict bleibt am
/// Gate. Fail-open über alles; garantierter Teardown der DAST-Topologie über RunningApp.</summary>
public sealed class DastAnalyzer(
    IAppRunner runner,
    DastOptions options,
    IChatClient chatClient,
    IDockerClient docker,
    ILoggerFactory loggerFactory,
    IReadOnlyList<AITool>? probeToolsOverride = null) : ISastAnalyzer
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DastAnalyzer>();

    public string Name => "dast";

    public async Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        if (!options.AppliesTo(workspace.ProjectId))
            return [];

        try
        {
            await using var app = await runner.RunAsync(workspace, ct);
            if (app is null) return [];   // nicht anwendbar / kam nicht hoch — Runner hat schon geloggt

            DastProbeSession? session = null;
            try
            {
                IReadOnlyList<AITool> tools;
                if (probeToolsOverride is not null)
                {
                    tools = probeToolsOverride;   // Testnaht: kein echter MCP-Server
                }
                else
                {
                    session = await DastProbeSession.StartAsync(docker, options, app.ProbeContainerName, loggerFactory, ct);
                    tools = session.Tools;
                }

                var raw = await RunProbeLoopAsync(app.InternalUrl, tools, ct);
                return ParseFindings(raw);
            }
            finally
            {
                if (session is not null) await session.DisposeAsync();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // echter Aufrufer-Abbruch propagiert
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DAST-Probing abgebrochen — Review läuft ohne dynamische Funde weiter.");
            return [];
        }
    }

    private async Task<string> RunProbeLoopAsync(string appUrl, IReadOnlyList<AITool> tools, CancellationToken ct)
    {
        var client = tools.Count > 0
            ? chatClient.AsBuilder().UseFunctionInvocation(loggerFactory,
                c => c.MaximumIterationsPerRequest = Math.Max(1, options.MaxProbeSteps)).Build()
            : chatClient;
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, DastProbePrompt.System(appUrl, options.MaxProbeSteps)),
            new(ChatRole.User, $"Probe the app at {appUrl} now and return the findings JSON."),
        };
        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        if (tools.Count > 0) chatOptions.Tools = [.. tools];
        var response = await client.GetResponseAsync(messages, chatOptions, ct);
        return response.Text;
    }

    /// <summary>Non-JSON / Schema-Fehler ⇒ leere Liste (Grounding-Schritt, nicht fail-closed).</summary>
    private IReadOnlyList<ScanFinding> ParseFindings(string text)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<ProbeResult>(text, JsonOpts);
            if (doc?.Findings is not { Count: > 0 }) return [];
            return doc.Findings
                .Where(f => f is not null)
                .Select(f => new ScanFinding("dast", FindingCategory.Dast, MapSeverity(f!.Severity),
                    $"{f.Summary} ({f.Endpoint})"))
                .ToList();
        }
        catch (JsonException)
        {
            _logger.LogInformation("DAST: Probing-Antwort war kein gültiges JSON — keine dynamischen Funde.");
            return [];
        }
    }

    private static FindingSeverity MapSeverity(string? s) => s?.ToLowerInvariant() switch
    {
        "high" => FindingSeverity.High,
        "medium" => FindingSeverity.Medium,
        _ => FindingSeverity.Low,
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private sealed record ProbeResult(List<ProbeFinding?>? Findings);
    private sealed record ProbeFinding(string? Severity, string? Endpoint, string? Summary);
}
```

> **Implementer note on `FindingSeverity`:** use the enum values that already exist in `src/Naudit.Core/Models/` (the SAST findings use it). If the members are named differently (e.g. `Info`/`Critical`), map accordingly and keep three tiers.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DastAnalyzerTests`
Expected: PASS (2).

- [ ] **Step 6: Full suite**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS — 706.

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Infrastructure/Dast/DastProbePrompt.cs src/Naudit.Infrastructure/Dast/DastAnalyzer.cs tests/Naudit.Tests
git commit -m "feat(dast): DastAnalyzer — Runner + Probing-Loop + JSON→ScanFinding(Dast)"
```

---

### Task 6: `DastAnalyzer` failure paths + guaranteed teardown

**Files:**
- Test: `tests/Naudit.Tests/DastAnalyzerTests.cs` (extend)
- Modify (only if a test proves it necessary): `src/Naudit.Infrastructure/Dast/DastAnalyzer.cs`

- [ ] **Step 1: Write the failing tests**

Append:

```csharp
    [Fact]
    public async Task Analyze_appNeverStarts_returnsEmpty()   // runner liefert null
    {
        var app = new FakeAppRunner { ReturnNull = true };
        var analyzer = new DastAnalyzer(app, Options(), new FakeChatClient("{\"findings\":[]}"),
            new FakeDockerClient(), NullLoggerFactory.Instance, probeToolsOverride: []);

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
        Assert.True(app.RunCalled);
    }

    [Fact]
    public async Task Analyze_nonJsonModelOutput_returnsEmpty_andTearsDown()
    {
        var app = new FakeAppRunner();
        var analyzer = new DastAnalyzer(app, Options(), new FakeChatClient("I could not access the app."),
            new FakeDockerClient(), NullLoggerFactory.Instance, probeToolsOverride: []);

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
        Assert.True(app.Disposed);
    }

    [Fact]
    public async Task Analyze_probeSessionThrows_returnsEmpty_andTearsDownApp()
    {
        var app = new FakeAppRunner();
        // probeToolsOverride null ⇒ echter DastProbeSession.StartAsync gegen den leeren FakeDockerClient ⇒ wirft
        var analyzer = new DastAnalyzer(app, Options(), new FakeChatClient("{\"findings\":[]}"),
            new FakeDockerClient(), NullLoggerFactory.Instance, probeToolsOverride: null);

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
        Assert.True(app.Disposed);   // App-Teardown trotz Probe-Fehler garantiert
    }

    [Fact]
    public async Task Analyze_callerCancelled_propagates()
    {
        var app = new FakeAppRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var analyzer = new DastAnalyzer(app, Options(), new FakeChatClient("{\"findings\":[]}"),
            new FakeDockerClient(), NullLoggerFactory.Instance, probeToolsOverride: []);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync(new Ws("/tmp/x"), [], cts.Token));
    }
```

Extend `FakeAppRunner` with `ReturnNull` (RunAsync returns null) and make its `RunAsync` honour a cancelled token by throwing `OperationCanceledException` (so the caller-cancellation test exercises the real propagation path). If the empty-`FakeDockerClient` `ExecStreamAsync` returns a stream on which `McpClient.CreateAsync` hangs rather than throwing, the Task-4 handshake timeout covers it; keep this test's assertion on teardown, and if it is slow, give `DastProbeSession` a short test-visible timeout via `DastOptions` (add `ProbeStartTimeout` default 30s only if needed — otherwise skip).

- [ ] **Step 2: Run tests to verify they fail / pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DastAnalyzerTests`
Expected: the happy-path implementation from Task 5 already satisfies most; fix `DastAnalyzer` minimally for any gap (never the test). Likely all pass immediately except possibly the probe-session-throws timing — address per the note above.

- [ ] **Step 3: Full suite**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS — 710.

- [ ] **Step 4: Commit**

```bash
git add src/Naudit.Infrastructure/Dast tests/Naudit.Tests
git commit -m "test(dast): Analyzer-Fehlerpfade — App-Fail, non-JSON, Probe-Fehler, Caller-Cancel; Teardown garantiert"
```

---

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

### Task 8: Documentation

**Files:**
- Modify: `docs/dast.md`, `CLAUDE.md`

- [ ] **Step 1: Extend `docs/dast.md`**

Add/expand these sections (English):
1. **Probing (PR 2)** — the runner now HAS a caller: `DastAnalyzer` boots the app, drives the Playwright-MCP server over `docker exec` stdio (no ports, no `docker` CLI in the image — raw Engine-API duplex exec), runs a bounded agentic loop (`MaxProbeSteps`, default 12) on the **global** chat client, and feeds JSON observations as `FindingCategory.Dast` grounding. Verdict stays LLM-driven; DAST never gates.
2. **Part B correction** — `"dast"` is now enabled purely by `Naudit:Review:Dast:Enabled` + the `Projects` allowlist (the analyzer registers itself when DAST is on; it is **not** a `Naudit:Sast:Analyzers` entry). Update any Part-B text that said the analyzer wasn't wired yet.
3. **Config** — add `MaxProbeSteps` (default 12, DB-managed) and `ProbeMcpArgv` (env-only list, default `node /app/cli.js --headless --browser chromium --no-sandbox`) to the table.
4. **Operational caveat (from the E2E gate)** — an app image that starts as **root and drops privileges** (e.g. stock nginx) will fail to start under `CapDrop: [ALL]`; that is the sandbox working as designed. Such apps must run as a non-root user in their Dockerfile to be DAST-probeable. State it plainly.
5. **Manual gate** — note the `NAUDIT_TEST_DOCKER=1 … SocketDockerClientTests` duplex-exec round-trip and that a full live probe (real app + real Playwright-MCP + a model that supports the tool-loop + JSON) is the pre-prod gate, inheriting the MCP #54 gate.

- [ ] **Step 2: Update the `CLAUDE.md` DAST bullet**

Extend the existing DAST bullet: PR 2 adds `DastAnalyzer : ISastAnalyzer` + `DastProbeSession` (Playwright-MCP over raw duplex `docker exec`, `StreamClientTransport`), `FindingCategory.Dast`, `MaxProbeSteps`; nothing-calls-the-runner is no longer true — the analyzer is the caller, registered when `Dast:Enabled`. Keep it one bullet.

- [ ] **Step 3: Full suite (docs-only, still green)**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS — 712.

- [ ] **Step 4: Commit**

```bash
git add docs/dast.md CLAUDE.md
git commit -m "docs(dast): Probing-Analyzer dokumentiert (exec-stdio-MCP, MaxProbeSteps, root-drop-Caveat)"
```

---

## Manual verification gate (before enabling DAST in prod)

CI never touches real Docker or a live model. Run once by hand:

1. `NAUDIT_TEST_DOCKER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SocketDockerClientTests` — duplex-exec round-trip green.
2. Point `Naudit:Review:Dast:Projects` at a small web repo with a non-root `Dockerfile`, enable DAST + a model that supports MCP function-calling + JSON response format, open a PR, and confirm: app + probe containers on `naudit-dast-net-*`, the MCP server answers `ListTools` over exec-stdio, the loop stays within `MaxProbeSteps`, any observations appear as a "DAST (dynamic)" grounding block in the review, and after the review no `naudit-dast-*` container/network/image remains. This inherits the #54 MCP gate (JSON + tool-loop coexistence on the target model).
3. Kill Naudit mid-probe; the PR-1 `DastOrphanSweeper` clears leftovers on restart.

## Out of scope (future)

Deterministic scanner (ZAP/Nuclei) as a second dynamic `ISastAnalyzer`; active attack scans; auth/seed data; multi-service compose; parallel DAST across concurrent CI-inline reviews (today reviews are sequential); bounded teardown/sweep timeouts and BuildKit `version=2` (carried follow-ups from PR 1).
