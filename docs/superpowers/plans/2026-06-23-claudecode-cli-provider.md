# ClaudeCode-CLI-Provider Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Einen vierten AI-Provider `ClaudeCode` ergänzen, der den Review über die lokal installierte `claude` CLI (headless) laufen lässt — „Abo statt API-Key", als reiner Provider-Tausch hinter der MEAI-Abstraktion.

**Architecture:** Neuer `ClaudeCodeChatClient : IChatClient` in `Naudit.Infrastructure`, der die `ChatMessage`-Liste auf einen `claude -p`-Subprozess abbildet (System-Prompt überschreiben, Diff über stdin, Tools aus, ein Turn), das JSON-Envelope parst und `.result` als `ChatResponse.Text` zurückgibt. Der Subprozess läuft hinter einer dünnen `IProcessRunner`-Naht (testbar mit Stub). `AiClientFactory` bekommt einen `case`; **`Naudit.Core` bleibt unangetastet**, Auswahl config-only über `Naudit:Ai:Provider=ClaudeCode`.

**Tech Stack:** .NET 10, Microsoft.Extensions.AI 10.7.0 (`IChatClient`), `System.Diagnostics.Process`, `System.Text.Json`; Tests mit xUnit. Die `claude` CLI ist eine **Umgebungs-Vorbedingung** (wird hier nicht installiert); Tests laufen ohne sie (Stub-Runner + POSIX-`cat`/`sleep`).

## Global Constraints

- Solution-Datei ist **`Naudit.slnx`** (nicht `Naudit.sln`). Build/Test: `dotnet build Naudit.slnx` / `dotnet test Naudit.slnx`.
- **Core nur an MEAI-Abstractions.** Dieses Feature berührt **ausschließlich** `Naudit.Infrastructure`, `tests/Naudit.Tests` und `docs/` — **kein** `Naudit.Core`, kein `Naudit.Web`.
- **Code-Kommentare auf Deutsch**, **Doku auf Englisch** (wie die übrige `docs/`).
- **MEAI GA-Namen** sind versionssensitiv (`IChatClient.GetResponseAsync`, `ChatResponse.Text`, `ChatResponseUpdate`). Meldet `dotnet build` hier einen fehlenden Member → Paketversion, nicht Logik (⚠️ API-Check).
- **Exakte CLI-Flags** (an der Doku gepinnt, `code.claude.com/docs/en/cli-reference`): `-p`/`--print`, `--output-format json`, `--model <alias|name>`, `--max-turns 1`, `--system-prompt <text>` (ersetzt den **gesamten** System-Prompt), `--tools ""` (deaktiviert **alle** Built-in-Tools; MCP nicht betroffen — wir konfigurieren keine).
- **JSON-Envelope** (`--output-format json`): relevante Felder `subtype` (string), `is_error` (bool), `result` (string mit dem Modelltext). Erfolg = `is_error == false` **und** `subtype == "success"`.
- **Fail-closed:** jeder Fehlerfall wirft eine Exception; **nie** ein Schein-Review zurückgeben.
- **Commits:** Conventional Commits, deutsche Beschreibung, Trailer `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`. Branch: `feat/claudecode-provider` (existiert bereits, enthält den Spec-Commit).

## File Structure

```
src/Naudit.Infrastructure/Ai/ClaudeCode/
  IProcessRunner.cs           # NEW: IProcessRunner + ProcessSpec + ProcessResult (Naht)
  SystemProcessRunner.cs      # NEW: System.Diagnostics.Process-Implementierung
  ClaudeCodeChatClient.cs     # NEW: IChatClient-Adapter (CLI-Aufruf + Envelope-Parsing)
src/Naudit.Infrastructure/Ai/
  AiOptions.cs                # MODIFY: enum-Wert ClaudeCode
  AiClientFactory.cs          # MODIFY: case AiProvider.ClaudeCode
tests/Naudit.Tests/
  Fakes/StubProcessRunner.cs  # NEW: testbarer IProcessRunner
  SystemProcessRunnerTests.cs # NEW: POSIX-hermetischer Plumbing-Test
  ClaudeCodeChatClientTests.cs# NEW: Adapter-Tests (Stub-Runner)
  AiClientFactoryTests.cs     # MODIFY: ClaudeCode-Fall
docs/
  claudecode-provider.md      # NEW: Vorbedingung + setup-token + Verhalten/Non-Goals
  configuration.md            # MODIFY: Provider-Zeile + ClaudeCode-Block
```

---

### Task 1: Prozess-Naht (`IProcessRunner` + `SystemProcessRunner`)

**Files:**
- Create: `src/Naudit.Infrastructure/Ai/ClaudeCode/IProcessRunner.cs`
- Create: `src/Naudit.Infrastructure/Ai/ClaudeCode/SystemProcessRunner.cs`
- Test: `tests/Naudit.Tests/SystemProcessRunnerTests.cs`

**Interfaces:**
- Produces:
  - `interface IProcessRunner { Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default); }`
  - `record ProcessSpec(string FileName, IReadOnlyList<string> Arguments, string? StdIn, IReadOnlyDictionary<string,string?>? Environment, string? WorkingDirectory, TimeSpan Timeout)`
  - `record ProcessResult(int ExitCode, string StdOut, string StdErr)`
  - `class SystemProcessRunner : IProcessRunner` (Default-Impl)

- [ ] **Step 1: Write the failing test**

`tests/Naudit.Tests/SystemProcessRunnerTests.cs`:
```csharp
using Naudit.Infrastructure.Ai.ClaudeCode;
using Xunit;

namespace Naudit.Tests;

public class SystemProcessRunnerTests
{
    // POSIX-only: nutzt `cat`/`sleep`. Auf Nicht-POSIX still überspringen.
    private static bool Posix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    [Fact]
    public async Task RunAsync_pipesStdInToStdOut_andReportsExitZero()
    {
        if (!Posix) return;
        var runner = new SystemProcessRunner();
        var spec = new ProcessSpec("cat", Array.Empty<string>(), "hallo welt",
            Environment: null, WorkingDirectory: null, Timeout: TimeSpan.FromSeconds(10));

        var result = await runner.RunAsync(spec);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("hallo welt", result.StdOut);
    }

    [Fact]
    public async Task RunAsync_killsOnTimeout_andThrowsTimeout()
    {
        if (!Posix) return;
        var runner = new SystemProcessRunner();
        var spec = new ProcessSpec("sleep", new[] { "5" }, StdIn: null,
            Environment: null, WorkingDirectory: null, Timeout: TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync(spec));
    }

    [Fact]
    public async Task RunAsync_missingBinary_throwsInvalidOperation()
    {
        var runner = new SystemProcessRunner();
        var spec = new ProcessSpec("naudit-no-such-binary-xyz", Array.Empty<string>(), StdIn: null,
            Environment: null, WorkingDirectory: null, Timeout: TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(spec));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~SystemProcessRunnerTests"`
Expected: FAIL — Compile-Fehler „type or namespace `IProcessRunner`/`ProcessSpec`/`SystemProcessRunner` not found".

- [ ] **Step 3: Write the interface + records**

`src/Naudit.Infrastructure/Ai/ClaudeCode/IProcessRunner.cs`:
```csharp
namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>Dünne, testbare Naht über einen Subprozess (vermeidet echtes `claude` im Test).</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default);
}

/// <param name="Environment">Additiv zur geerbten Prozess-Umgebung.</param>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? StdIn,
    IReadOnlyDictionary<string, string?>? Environment,
    string? WorkingDirectory,
    TimeSpan Timeout);

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
```

- [ ] **Step 4: Write the `SystemProcessRunner` implementation**

`src/Naudit.Infrastructure/Ai/ClaudeCode/SystemProcessRunner.cs`:
```csharp
using System.ComponentModel;
using System.Diagnostics;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>Führt einen Subprozess aus: schreibt stdin, liest stdout/stderr, killt bei Timeout/Cancel.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // ArgumentList: kein Shell-Quoting, leeres Argument (--tools "") bleibt erhalten.
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var arg in spec.Arguments)
            psi.ArgumentList.Add(arg);
        if (spec.Environment is not null)
            foreach (var kv in spec.Environment)
                psi.Environment[kv.Key] = kv.Value; // psi.Environment ist bereits mit der Eltern-Env vorbefüllt

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Konnte '{spec.FileName}' nicht starten — installiert und auf PATH? ({ex.Message})", ex);
        }

        // stdin vollständig schreiben + schließen (claude `-p` liest bis EOF, bevor es ausgibt → kein Deadlock).
        if (spec.StdIn is not null)
            await process.StandardInput.WriteAsync(spec.StdIn.AsMemory(), ct);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(spec.Timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* schon beendet */ }
            if (ct.IsCancellationRequested)
                throw; // externer Cancel: durchreichen
            throw new TimeoutException(
                $"'{spec.FileName}' überschritt das Timeout von {spec.Timeout.TotalSeconds:0}s.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~SystemProcessRunnerTests"`
Expected: PASS (3 Tests).

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Ai/ClaudeCode/IProcessRunner.cs \
        src/Naudit.Infrastructure/Ai/ClaudeCode/SystemProcessRunner.cs \
        tests/Naudit.Tests/SystemProcessRunnerTests.cs
git commit -m "feat(ai): IProcessRunner-Naht + SystemProcessRunner für Subprozesse" \
           -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: `ClaudeCodeChatClient : IChatClient`

**Files:**
- Create: `src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs`
- Create: `tests/Naudit.Tests/Fakes/StubProcessRunner.cs`
- Test: `tests/Naudit.Tests/ClaudeCodeChatClientTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `ProcessSpec`, `ProcessResult` (Task 1); `AiOptions` (`Model`, `ApiKey`, `TimeoutSeconds`).
- Produces: `class ClaudeCodeChatClient(AiOptions aiOptions, IProcessRunner runner) : IChatClient`.

- [ ] **Step 1: Write the Stub-Runner (Test-Helfer)**

`tests/Naudit.Tests/Fakes/StubProcessRunner.cs`:
```csharp
using Naudit.Infrastructure.Ai.ClaudeCode;

namespace Naudit.Tests.Fakes;

// Fängt den ProcessSpec ab und liefert eine vorgegebene Antwort (oder wirft).
internal sealed class StubProcessRunner(Func<ProcessSpec, ProcessResult> responder) : IProcessRunner
{
    public ProcessSpec? LastSpec { get; private set; }

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
    {
        LastSpec = spec;
        return Task.FromResult(responder(spec)); // wirft responder, propagiert es RunAsync synchron
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/Naudit.Tests/ClaudeCodeChatClientTests.cs`:
```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ClaudeCodeChatClientTests
{
    // Baut ein --output-format-json-Envelope wie die CLI es liefert.
    private static string Envelope(string? result, bool isError = false, string subtype = "success")
        => JsonSerializer.Serialize(new { type = "result", subtype, is_error = isError, result });

    private static ChatMessage[] Messages() =>
    [
        new(ChatRole.System, "SYS-PROMPT"),
        new(ChatRole.User, "USER-DIFF"),
    ];

    private static ClaudeCodeChatClient Client(Func<ProcessSpec, ProcessResult> responder, AiOptions? opts = null)
        => new(opts ?? new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" },
               new StubProcessRunner(responder));

    [Fact]
    public async Task GetResponseAsync_buildsHeadlessArgs_andPipesUserMessageToStdIn()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub);

        await client.GetResponseAsync(Messages());

        var args = stub.LastSpec!.Arguments;
        Assert.Contains("-p", args);
        Assert.Contains("--output-format", args);
        Assert.Contains("json", args);
        Assert.Contains("--max-turns", args);
        Assert.Contains("--tools", args);                 // gefolgt von "" → Tools aus
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Equal("sonnet", args[args.IndexOf("--model") + 1]);
        Assert.Equal("SYS-PROMPT", args[args.IndexOf("--system-prompt") + 1]);
        Assert.Equal("USER-DIFF", stub.LastSpec.StdIn);   // Diff geht über stdin, nicht als Arg
    }

    [Fact]
    public async Task GetResponseAsync_returnsResultFieldAsText()
    {
        var client = Client(_ => new ProcessResult(0, Envelope("{\"verdict\":\"approve\"}"), ""));

        var response = await client.GetResponseAsync(Messages());

        Assert.Equal("{\"verdict\":\"approve\"}", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_stripsJsonCodeFences()
    {
        var client = Client(_ => new ProcessResult(0, Envelope("```json\n{\"x\":1}\n```"), ""));

        var response = await client.GetResponseAsync(Messages());

        Assert.Equal("{\"x\":1}", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_defaultsModelToSonnet_whenUnset()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var client = new ClaudeCodeChatClient(new AiOptions { Provider = AiProvider.ClaudeCode }, stub);

        await client.GetResponseAsync(Messages());

        Assert.Equal("sonnet", stub.LastSpec!.Arguments[stub.LastSpec.Arguments.IndexOf("--model") + 1]);
    }

    [Fact]
    public async Task GetResponseAsync_passesApiKeyAsOAuthTokenEnv()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet", ApiKey = "tok-123" }, stub);

        await client.GetResponseAsync(Messages());

        Assert.NotNull(stub.LastSpec!.Environment);
        Assert.Equal("tok-123", stub.LastSpec.Environment!["CLAUDE_CODE_OAUTH_TOKEN"]);
    }

    [Fact]
    public async Task GetResponseAsync_nonZeroExit_throws()
    {
        var client = Client(_ => new ProcessResult(1, "", "boom"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task GetResponseAsync_isErrorEnvelope_throws()
    {
        var client = Client(_ => new ProcessResult(0, Envelope("x", isError: true), ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));
    }

    [Fact]
    public async Task GetResponseAsync_nonSuccessSubtype_throws()
    {
        var client = Client(_ => new ProcessResult(0, Envelope("x", subtype: "error_max_turns"), ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));
    }

    [Fact]
    public async Task GetResponseAsync_emptyResult_throws()
    {
        var client = Client(_ => new ProcessResult(0, Envelope(""), ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~ClaudeCodeChatClientTests"`
Expected: FAIL — `ClaudeCodeChatClient` existiert nicht.

- [ ] **Step 4: Write the `ClaudeCodeChatClient` implementation**

`src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs`:
```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>
/// IChatClient-Adapter, der die `claude` CLI headless aufruft (Abo-Auth statt API-Key).
/// Reiner Single-Shot: System-Prompt überschrieben, Diff über stdin, Tools aus, ein Turn.
/// </summary>
public sealed class ClaudeCodeChatClient(AiOptions aiOptions, IProcessRunner runner) : IChatClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var system = string.Join("\n\n", list.Where(m => m.Role == ChatRole.System)
            .Select(m => m.Text).Where(t => !string.IsNullOrEmpty(t)));
        var user = string.Join("\n\n", list.Where(m => m.Role != ChatRole.System)
            .Select(m => m.Text).Where(t => !string.IsNullOrEmpty(t)));

        var model = string.IsNullOrWhiteSpace(aiOptions.Model) ? "sonnet" : aiOptions.Model;

        // Tools aus, ein Turn, JSON-Envelope; Reihenfolge egal, aber --tools muss ein Folge-Argument "" haben.
        var args = new List<string>
        {
            "-p", "--output-format", "json", "--max-turns", "1", "--tools", "", "--model", model,
        };
        if (!string.IsNullOrEmpty(system))
        {
            args.Add("--system-prompt"); // ersetzt den GESAMTEN System-Prompt (kein Coding-Agent, kein CLAUDE.md)
            args.Add(system);
        }

        // Auth: i. d. R. über die geerbte Umgebung (CLAUDE_CODE_OAUTH_TOKEN). Optional aus der Config.
        Dictionary<string, string?>? env = null;
        if (!string.IsNullOrWhiteSpace(aiOptions.ApiKey))
            env = new Dictionary<string, string?> { ["CLAUDE_CODE_OAUTH_TOKEN"] = aiOptions.ApiKey };

        var spec = new ProcessSpec(
            FileName: "claude",
            Arguments: args,
            StdIn: user,
            Environment: env,
            WorkingDirectory: Path.GetTempPath(), // neutrales CWD: kein ambient CLAUDE.md
            Timeout: TimeSpan.FromSeconds(aiOptions.TimeoutSeconds));

        var result = await runner.RunAsync(spec, cancellationToken);

        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"claude beendete mit Exit-Code {result.ExitCode}. stderr: {result.StdErr}");

        var envelope = JsonSerializer.Deserialize<ClaudeResult>(result.StdOut, JsonOpts)
            ?? throw new InvalidOperationException("claude lieferte kein parsebares JSON-Envelope.");

        if (envelope.IsError || envelope.Subtype != "success")
            throw new InvalidOperationException(
                $"claude meldete einen Fehler (subtype='{envelope.Subtype}'). stderr: {result.StdErr}");

        if (string.IsNullOrWhiteSpace(envelope.Result))
            throw new InvalidOperationException("claude lieferte ein leeres 'result'.");

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, StripJsonFences(envelope.Result)));
    }

    // ReviewService nutzt nur die non-streaming Variante; hier ein dünner Wrapper übers Einzelergebnis.
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }

    // Entfernt umschließende ```json … ``` / ``` … ```-Fences, falls das Modell welche setzt.
    private static string StripJsonFences(string s)
    {
        var t = s.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal))
            return t;
        var firstNewline = t.IndexOf('\n');
        if (firstNewline >= 0)
            t = t[(firstNewline + 1)..];
        if (t.EndsWith("```", StringComparison.Ordinal))
            t = t[..^3];
        return t.Trim();
    }

    // Nur die vom Adapter benötigten Felder des CLI-Envelopes.
    private sealed record ClaudeResult(
        [property: JsonPropertyName("subtype")] string? Subtype,
        [property: JsonPropertyName("is_error")] bool IsError,
        [property: JsonPropertyName("result")] string? Result);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~ClaudeCodeChatClientTests"`
Expected: PASS (9 Tests). Falls ein Compile-Fehler an `ChatResponse`/`ChatResponseUpdate`/`ChatMessage.Text` auftritt → MEAI-Versionsabweichung (⚠️ API-Check), nicht Logik.

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs \
        tests/Naudit.Tests/Fakes/StubProcessRunner.cs \
        tests/Naudit.Tests/ClaudeCodeChatClientTests.cs
git commit -m "feat(ai): ClaudeCodeChatClient — claude CLI headless als IChatClient" \
           -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: Factory-Verdrahtung + Konfiguration + Doku

**Files:**
- Modify: `src/Naudit.Infrastructure/Ai/AiOptions.cs` (enum)
- Modify: `src/Naudit.Infrastructure/Ai/AiClientFactory.cs` (case + using)
- Test: `tests/Naudit.Tests/AiClientFactoryTests.cs`
- Create: `docs/claudecode-provider.md`
- Modify: `docs/configuration.md`

**Interfaces:**
- Consumes: `ClaudeCodeChatClient`, `SystemProcessRunner` (Task 1+2).
- Produces: `AiProvider.ClaudeCode`; `AiClientFactory.Create` liefert dafür einen `ClaudeCodeChatClient`.

- [ ] **Step 1: Write the failing test**

In `tests/Naudit.Tests/AiClientFactoryTests.cs` ergänzen (Imports `Naudit.Infrastructure.Ai.ClaudeCode;` oben hinzufügen):
```csharp
    [Fact]
    public void Create_claudeCode_returnsClaudeCodeChatClient()
    {
        // Kein ApiKey nötig — Auth läuft über die Umgebung (CLAUDE_CODE_OAUTH_TOKEN).
        var client = AiClientFactory.Create(new AiOptions
        {
            Provider = AiProvider.ClaudeCode,
            Model = "sonnet",
        });

        Assert.IsType<ClaudeCodeChatClient>(client);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~AiClientFactoryTests"`
Expected: FAIL — `AiProvider.ClaudeCode` existiert nicht (Compile-Fehler).

- [ ] **Step 3: Add the enum value**

`src/Naudit.Infrastructure/Ai/AiOptions.cs` — Zeile 3 ändern:
```csharp
public enum AiProvider { Anthropic, Ollama, OpenAICompatible, ClaudeCode }
```

- [ ] **Step 4: Add the factory case**

`src/Naudit.Infrastructure/Ai/AiClientFactory.cs` — oben den Using ergänzen:
```csharp
using Naudit.Infrastructure.Ai.ClaudeCode;
```
und im `switch` (vor `default:`) den Fall einfügen:
```csharp
            case AiProvider.ClaudeCode:
                // Kein RequireApiKey: die CLI authentifiziert über die Umgebung (Abo statt Key).
                return new ClaudeCodeChatClient(options, new SystemProcessRunner());
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~AiClientFactoryTests"`
Expected: PASS (4 Tests).

- [ ] **Step 6: Add the configuration docs**

`docs/configuration.md` — die Provider-Zeile in der Key-Tabelle ersetzen:
```markdown
| `Naudit:Ai:Provider` | `Ollama` \| `Anthropic` \| `OpenAICompatible` \| `ClaudeCode` |
```
und unter „## Choosing an AI provider" am Ende des Bash-Blocks ergänzen:
```bash
# ClaudeCode (local `claude` CLI, subscription instead of API key — see docs/claudecode-provider.md)
dotnet user-secrets set "Naudit:Ai:Provider" "ClaudeCode" --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model"    "sonnet"     --project src/Naudit.Web
# Auth: set CLAUDE_CODE_OAUTH_TOKEN in the environment (from `claude setup-token`); no Naudit:Ai:ApiKey needed.
```

- [ ] **Step 7: Create the provider doc**

`docs/claudecode-provider.md`:
```markdown
# ClaudeCode provider (local `claude` CLI)

The `ClaudeCode` AI provider runs the review through the **locally installed `claude`
CLI** (Claude Code) in headless mode instead of calling an SDK/HTTP endpoint. Its purpose
is **subscription instead of API key**: the CLI authenticates with your logged-in Claude
account (Pro/Max), so reviews are not billed per token.

It is a plain provider swap behind the MEAI abstraction — the review is still a single
pass over the diff returning the same JSON. It is **not** an agentic review (no repository
access, no tools).

## Precondition

`claude` must be installed and authenticated **on the machine that runs Naudit** (this is
not installed or managed by Naudit):

```bash
# Install (see the Claude Code docs for your platform)
npm install -g @anthropic-ai/claude-code

# Headless auth for Pro/Max — produces a long-lived OAuth token
claude setup-token
export CLAUDE_CODE_OAUTH_TOKEN=<token-from-setup-token>
```

In a container the token is an environment variable (`CLAUDE_CODE_OAUTH_TOKEN`); Naudit
passes it through to the `claude` subprocess. Alternatively set `Naudit:Ai:ApiKey` — Naudit
forwards it as `CLAUDE_CODE_OAUTH_TOKEN` to the subprocess.

## Configuration

```bash
dotnet user-secrets set "Naudit:Ai:Provider" "ClaudeCode" --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model"    "sonnet"     --project src/Naudit.Web
```

`Naudit:Ai:Model` accepts an alias (`sonnet`, `opus`, `haiku`, `fable`) or a full model id;
empty defaults to `sonnet`. `Naudit:Ai:TimeoutSeconds` bounds the subprocess (default 600).

## How it works

Naudit invokes `claude -p --output-format json --max-turns 1 --tools "" --model <model>
--system-prompt <prompt>` and pipes the annotated diff to **stdin**. It parses the JSON
envelope and uses its `result` field as the model output. Any non-zero exit, `is_error`,
non-`success` subtype, empty result, or timeout fails the review (fail-closed) — no comment
is posted.

## Non-goals

- No Dockerfile changes here — `claude` is an environment precondition. Baking Node + the
  CLI into the deployed image is a separate, later step.
- No agentic review (no repo access / tools / MCP), no streaming, no multi-turn sessions.
- Future hardening option: `claude --json-schema` for schema-validated structured output.
```

- [ ] **Step 8: Run the full suite**

Run: `dotnet test Naudit.slnx`
Expected: PASS — alle bisherigen Tests plus die neuen (Task 1–3) grün.

- [ ] **Step 9: Commit**

```bash
git add src/Naudit.Infrastructure/Ai/AiOptions.cs \
        src/Naudit.Infrastructure/Ai/AiClientFactory.cs \
        tests/Naudit.Tests/AiClientFactoryTests.cs \
        docs/configuration.md docs/claudecode-provider.md
git commit -m "feat(ai): ClaudeCode-Provider config-only verdrahten + Doku" \
           -m "Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage** (gegen `docs/superpowers/specs/2026-06-23-claudecode-cli-provider-design.md`):
- Neuer `AiProvider.ClaudeCode` + Factory-`case` → Task 3 ✓
- `ClaudeCodeChatClient : IChatClient` (System→`--system-prompt`, User→stdin, `-p`/`json`/`--max-turns 1`/Tools aus/`--model`, Envelope→`.result`, Fences strippen, Timeout) → Task 2 ✓
- `IProcessRunner`-Naht + `SystemProcessRunner` → Task 1 ✓
- Auth via Env / optional `ApiKey`→`CLAUDE_CODE_OAUTH_TOKEN` → Task 2 ✓
- Modell-Default `sonnet` → Task 2 ✓
- Fail-closed (exit≠0 / is_error / subtype / leeres result / Timeout) → Task 1+2 ✓
- Tests analog `StubHttpMessageHandler` (Stub-Runner) + Envelope-Mapping → Task 1+2 ✓
- `GetStreamingResponseAsync` dünner Wrapper, `GetService`/`Dispose` minimal → Task 2 ✓
- Doku (EN) Vorbedingung + `setup-token` → Task 3 ✓
- Core unangetastet, config-only → keine Core-/Web-Datei in irgendeinem Task ✓
- Non-Goals (kein Dockerfile/Sidecar/agentisch/Streaming) → dokumentiert, nicht implementiert ✓

**Placeholder scan:** keine TBD/TODO; jeder Code-Step zeigt vollständigen Code; jeder Test zeigt konkrete Assertions.

**Type consistency:** `IProcessRunner.RunAsync(ProcessSpec, CancellationToken)`, `ProcessSpec(FileName, Arguments, StdIn, Environment, WorkingDirectory, Timeout)`, `ProcessResult(ExitCode, StdOut, StdErr)`, `ClaudeCodeChatClient(AiOptions, IProcessRunner)` — in Task 1 definiert, in Task 2/3 identisch verwendet. `CLAUDE_CODE_OAUTH_TOKEN`, `--tools ""`, `subtype=="success"` durchgängig gleich.

## Verweise

- Spec: `docs/superpowers/specs/2026-06-23-claudecode-cli-provider-design.md`
- CLI-Flags geprüft: `https://code.claude.com/docs/en/cli-reference`
- Bestehende Factory/Optionen: `src/Naudit.Infrastructure/Ai/AiClientFactory.cs`, `AiOptions.cs`
- Vault: `1. Projects/Naudit/2026-06-23 ClaudeCode-CLI-Provider – Design.md`
