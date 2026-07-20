# Session-Sandbox Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Author-/Round-Robin-Claude-Sessions laufen in langlebigen Geschwister-Containern pro Account (warme Session + Isolation), gestartet über den Host-Docker-Socket — rein additiv, Default bleibt der heutige In-Process-Lauf.

**Architecture:** Ein dünner `IDockerClient` (eigener Mini-Client über den Unix-Socket, kein NuGet) trägt einen `SessionContainerManager` (Lifecycle: run/start/stop/rm, LRU-Cap, Locks, Adoption) und einen account-gebundenen `DockerSessionRunner : IProcessRunner`, den `SessionSelectionFactory` über eine neue `ISessionRunnerFactory`-Naht bezieht. Ein Hosted-Service-Sweeper pingt/adoptiert beim Start und stoppt idle Container. Überall Fail-Open auf den heutigen `SystemProcessRunner`.

**Tech Stack:** .NET 10, `SocketsHttpHandler.ConnectCallback` + `UnixDomainSocketEndPoint`, `System.Formats.Tar` (in-box), xUnit + Fakes.

**Spec:** `docs/superpowers/specs/2026-07-18-session-sandbox-design.md` (im Repo). Bewusste Abweichungen von der Spec (alle in Task 8 dokumentieren):

1. **Token pro Exec, nicht pro Container:** `CLAUDE_CODE_OAUTH_TOKEN` wandert als Exec-Env mit (frischer Token je Lauf, kein veralteter Token in der Container-Env sichtbar via `docker inspect`).
2. **Stdin ohne Stream-Hijacking:** Der Prompt wird per `PUT /containers/{id}/archive` (Tar-Upload) nach `/tmp/naudit-stdin` geschrieben und via `sh -c 'exec "$0" "$@" < /tmp/naudit-stdin'` umgeleitet — der Exec läuft ohne `AttachStdin`, stdout/stderr kommen als normaler HTTP-Response-Stream (multiplexte Frames).
3. **Start-Ping im Sweeper statt in `AddNauditInfrastructure`:** Der Sweeper pingt beim Start UND bei jedem Tick — die Sandbox heilt sich nach einem Socket-Ausfall selbst, DI bleibt synchron.
4. **Kein Laufzeit-`setgroups`:** Sich selbst eine supplementary group zu geben braucht CAP_SETGID — entfällt. Stattdessen: Ping-Probe + Log-Warnung; `group_add` wird in `docs/deployment.md` dokumentiert.
5. **`RemoveVolumeAsync` separat:** benannte Volumes werden von `DELETE /containers/{id}` nicht mitgelöscht — eigener API-Call beim Pool-Austritt/Token-Löschen.

## Global Constraints

- **Worktree:** Alle Kommandos laufen in `/home/bnau/workspace/Naudit/.claude/worktrees/feat-session-sandbox` (Branch `feat/session-sandbox`).
- **Solution-Datei ist `Naudit.slnx`** — `dotnet test Naudit.slnx`, niemals `Naudit.sln` (MSB1009).
- **Core-Regel:** `Naudit.Core` bleibt unberührt — alles Neue liegt in `Naudit.Infrastructure`/`Naudit.Web`/`src/frontend`.
- **Kein neues NuGet-Paket.** Einzige erlaubte Ausnahme: falls `BackgroundService`/`AddHostedService` in `Naudit.Infrastructure` nicht auflösbar ist, `<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.9" />` ergänzen (Versionslinie wie die übrigen 10.0.9-Pakete).
- **Code-Kommentare deutsch, Doku (docs/) englisch.**
- **Default-Verhalten unverändert:** `Naudit:Ai:SessionSandbox=None` (Default) ⇒ exakt der heutige Pfad (`SystemProcessRunner`), byte-identische CLI-Argumente.
- **Fail-Open über alles:** Ein Review scheitert nie an der Sandbox — jeder Docker-Plumbing-Fehler fällt auf den In-Process-Lauf zurück. `DockerUnavailableException` ist der einzige dafür gefangene Typ.
- **Commits:** einer pro Task, Präfix `feat(sandbox):` bzw. `docs(sandbox):`, Trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- `.superpowers/sdd/*` sind getrackte Scratch-Dateien — nie in Feature-Commits aufnehmen.

---

### Task 1: Konfiguration (`SessionSandbox`-Modus + Sandbox-Optionen + Settings-Katalog)

**Files:**
- Modify: `src/Naudit.Infrastructure/Ai/AiOptions.cs`
- Create: `src/Naudit.Infrastructure/Ai/Sandbox/SessionSandboxOptions.cs`
- Modify: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs` (nach der Zeile `new("Naudit:Ai:AuthorSessions:Model", false),`)
- Test: `tests/Naudit.Tests/SessionSandboxOptionsTests.cs`

**Interfaces:**
- Produces: `enum SessionSandbox { None, Docker }` und `AiOptions.SessionSandbox` (Default `None`); `SessionSandboxOptions` mit `IdleTimeout` (TimeSpan, Default 2 Tage), `MaxLiveContainers` (int, Default 5), `DockerSocketPath` (string, Default `/var/run/docker.sock`), `Image` (string?, Default null). Namespace `Naudit.Infrastructure.Ai.Sandbox`.

- [ ] **Step 1: Failing Test schreiben**

```csharp
using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

public class SessionSandboxOptionsTests
{
    [Fact]
    public void Binds_mode_and_subOptions()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Naudit:Ai:SessionSandbox"] = "Docker",
            ["Naudit:Ai:Sandbox:IdleTimeout"] = "1.12:00:00",
            ["Naudit:Ai:Sandbox:MaxLiveContainers"] = "3",
            ["Naudit:Ai:Sandbox:DockerSocketPath"] = "/run/user/1000/docker.sock",
            ["Naudit:Ai:Sandbox:Image"] = "ghcr.io/benediktnau/naudit:v1.2.3",
        }).Build();

        var ai = config.GetSection("Naudit:Ai").Get<AiOptions>()!;
        var sandbox = config.GetSection("Naudit:Ai:Sandbox").Get<SessionSandboxOptions>()!;

        Assert.Equal(SessionSandbox.Docker, ai.SessionSandbox);
        Assert.Equal(TimeSpan.FromHours(36), sandbox.IdleTimeout);
        Assert.Equal(3, sandbox.MaxLiveContainers);
        Assert.Equal("/run/user/1000/docker.sock", sandbox.DockerSocketPath);
        Assert.Equal("ghcr.io/benediktnau/naudit:v1.2.3", sandbox.Image);
    }

    [Fact]
    public void Defaults_areOff_withTwoDayIdle()
    {
        var config = new ConfigurationBuilder().Build();

        var ai = config.GetSection("Naudit:Ai").Get<AiOptions>() ?? new AiOptions();
        var sandbox = config.GetSection("Naudit:Ai:Sandbox").Get<SessionSandboxOptions>() ?? new SessionSandboxOptions();

        Assert.Equal(SessionSandbox.None, ai.SessionSandbox);
        Assert.Equal(TimeSpan.FromDays(2), sandbox.IdleTimeout);
        Assert.Equal(5, sandbox.MaxLiveContainers);
        Assert.Equal("/var/run/docker.sock", sandbox.DockerSocketPath);
        Assert.Null(sandbox.Image);
    }

    [Fact]
    public void Catalog_hasSandboxKeys()
    {
        Assert.True(SettingsCatalog.TryGet("Naudit:Ai:SessionSandbox", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Ai:Sandbox:IdleTimeout", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Ai:Sandbox:MaxLiveContainers", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Ai:Sandbox:DockerSocketPath", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Ai:Sandbox:Image", out _));
    }
}
```

- [ ] **Step 2: Test laufen lassen — muss ROT sein**

Run: `dotnet test Naudit.slnx --filter SessionSandboxOptionsTests 2>&1 | tail -5`
Expected: Compile-Fehler (`SessionSandbox`/`SessionSandboxOptions` existieren nicht) — das zählt als rot.

- [ ] **Step 3: Implementieren**

In `src/Naudit.Infrastructure/Ai/AiOptions.cs` — unter dem `SessionRouting`-Enum (Zeile 6) ergänzen:

```csharp
/// <summary>Wo Abo-Session-Läufe (Author/RoundRobin) ausgeführt werden: in-process (heutiges
/// Verhalten) oder in Geschwister-Containern pro Account über den Host-Docker-Socket.</summary>
public enum SessionSandbox { None, Docker }
```

und in der `AiOptions`-Klasse ans Ende:

```csharp
    /// <summary>Naudit:Ai:SessionSandbox — Default None = heutiger In-Process-Runner. Docker greift
    /// nur für Author-/RoundRobin-Routing; im Single-Modus (globaler Provider) ist es bedeutungslos.
    /// Sub-Optionen unter Naudit:Ai:Sandbox (SessionSandboxOptions).</summary>
    public SessionSandbox SessionSandbox { get; set; } = SessionSandbox.None;
```

Neu `src/Naudit.Infrastructure/Ai/Sandbox/SessionSandboxOptions.cs`:

```csharp
namespace Naudit.Infrastructure.Ai.Sandbox;

/// <summary>Section Naudit:Ai:Sandbox — Betriebsparameter der containerisierten Abo-Sessions.
/// Bewusst langes IdleTimeout: nicht der Sweeper, sondern MaxLiveContainers (LRU-Stopp) ist im
/// Alltag der Ressourcen-Begrenzer; der Sweeper ist das Netz für "Account tagelang still".</summary>
public sealed class SessionSandboxOptions
{
    /// <summary>Laufende Container, deren Account so lange keinen Review hatte, werden gestoppt
    /// (nicht entfernt — die warme Session liegt im Volume und überlebt den Stopp).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromDays(2);

    /// <summary>Obergrenze gleichzeitig laufender Session-Container; vor dem Start eines weiteren
    /// wird der am längsten ungenutzte gestoppt (LRU). Untergrenze 1 erzwingt der Manager.</summary>
    public int MaxLiveContainers { get; set; } = 5;

    /// <summary>Pfad zum Docker-Engine-Socket des Hosts.</summary>
    public string DockerSocketPath { get; set; } = "/var/run/docker.sock";

    /// <summary>Optionaler Image-Override; leer ⇒ Selbst-Inspektion (eigenes Image via
    /// `docker inspect $HOSTNAME`) — die claude-CLI ist im Naudit-Image bereits enthalten.</summary>
    public string? Image { get; set; }
}
```

In `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs` direkt nach `new("Naudit:Ai:AuthorSessions:Model", false),` einfügen:

```csharp
        new("Naudit:Ai:SessionSandbox", false),
        new("Naudit:Ai:Sandbox:IdleTimeout", false),
        new("Naudit:Ai:Sandbox:MaxLiveContainers", false),
        new("Naudit:Ai:Sandbox:DockerSocketPath", false),
        new("Naudit:Ai:Sandbox:Image", false),
```

- [ ] **Step 4: Test laufen lassen — muss GRÜN sein**

Run: `dotnet test Naudit.slnx --filter SessionSandboxOptionsTests 2>&1 | tail -3`
Expected: `Passed! - Failed: 0, Passed: 3`

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Ai/AiOptions.cs src/Naudit.Infrastructure/Ai/Sandbox/SessionSandboxOptions.cs src/Naudit.Infrastructure/Settings/SettingsCatalog.cs tests/Naudit.Tests/SessionSandboxOptionsTests.cs
git commit -m "feat(sandbox): Naudit:Ai:SessionSandbox-Modus + Sandbox-Optionen im Settings-Katalog

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Docker-Client-Naht (`IDockerClient`, Stream-Demux, `SocketDockerClient`)

**Files:**
- Create: `src/Naudit.Infrastructure/Docker/IDockerClient.cs`
- Create: `src/Naudit.Infrastructure/Docker/DockerStreamDemux.cs`
- Create: `src/Naudit.Infrastructure/Docker/SocketDockerClient.cs`
- Test: `tests/Naudit.Tests/DockerStreamDemuxTests.cs` (Unit, läuft immer)
- Test: `tests/Naudit.Tests/SocketDockerClientTests.cs` (Integration, opt-in via `NAUDIT_TEST_DOCKER=1` — Muster `NauditDbContextPostgresTests`: früher `return` wenn Env fehlt)

**Interfaces:**
- Produces (Namespace `Naudit.Infrastructure.Docker`):

```csharp
public interface IDockerClient
{
    Task<bool> PingAsync(CancellationToken ct = default);
    Task<string?> InspectSelfImageAsync(string hostname, CancellationToken ct = default);
    Task<ContainerInfo?> InspectContainerAsync(string name, CancellationToken ct = default);
    Task RunDetachedAsync(ContainerRunSpec spec, CancellationToken ct = default);
    Task StartAsync(string name, CancellationToken ct = default);
    Task StopAsync(string name, CancellationToken ct = default);
    Task RemoveContainerAsync(string name, CancellationToken ct = default);
    Task RemoveVolumeAsync(string name, CancellationToken ct = default);
    Task WriteFileAsync(string name, string directory, string fileName, string content, CancellationToken ct = default);
    Task<DockerExecResult> ExecAsync(string name, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string?>? environment, string workingDirectory, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerListEntry>> ListContainersAsync(string namePrefix, CancellationToken ct = default);
}

public sealed record ContainerInfo(bool Running);
public sealed record ContainerListEntry(string Name, bool Running);
public sealed record ContainerRunSpec(string Name, string Image, string VolumeName, string VolumeTarget, IReadOnlyList<string> Command);
public sealed record DockerExecResult(int ExitCode, string StdOut, string StdErr);
public sealed class DockerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
```

- `DockerStreamDemux.ReadAsync(Stream, CancellationToken) -> Task<(string StdOut, string StdErr)>` (statisch, public — von Tests direkt geprüft).

- [ ] **Step 1: Failing Demux-Test schreiben**

```csharp
using System.Buffers.Binary;
using System.Text;
using Naudit.Infrastructure.Docker;
using Xunit;

namespace Naudit.Tests;

public class DockerStreamDemuxTests
{
    private static byte[] Frame(byte streamType, string payload)
    {
        var data = Encoding.UTF8.GetBytes(payload);
        var frame = new byte[8 + data.Length];
        frame[0] = streamType;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)data.Length);
        data.CopyTo(frame, 8);
        return frame;
    }

    [Fact]
    public async Task Splits_stdout_and_stderr_frames()
    {
        var stream = new MemoryStream([.. Frame(1, "out-1 "), .. Frame(2, "err-1"), .. Frame(1, "out-2")]);

        var (stdout, stderr) = await DockerStreamDemux.ReadAsync(stream);

        Assert.Equal("out-1 out-2", stdout);
        Assert.Equal("err-1", stderr);
    }

    [Fact]
    public async Task EmptyStream_yieldsEmptyOutputs()
    {
        var (stdout, stderr) = await DockerStreamDemux.ReadAsync(new MemoryStream());
        Assert.Equal("", stdout);
        Assert.Equal("", stderr);
    }

    [Fact]
    public async Task TruncatedPayload_returnsWhatArrived()
    {
        var full = Frame(1, "hello");
        var truncated = full[..^2]; // Payload endet mitten im Frame (abrupter Verbindungsabriss)

        var (stdout, _) = await DockerStreamDemux.ReadAsync(new MemoryStream(truncated));

        Assert.Equal("hel", stdout);
    }

    [Fact]
    public async Task ZeroLengthFrame_isSkipped()
    {
        var stream = new MemoryStream([.. Frame(1, ""), .. Frame(2, "e")]);
        var (stdout, stderr) = await DockerStreamDemux.ReadAsync(stream);
        Assert.Equal("", stdout);
        Assert.Equal("e", stderr);
    }
}
```

- [ ] **Step 2: Rot verifizieren**

Run: `dotnet test Naudit.slnx --filter DockerStreamDemuxTests 2>&1 | tail -5`
Expected: Compile-Fehler (Typ fehlt).

- [ ] **Step 3: `IDockerClient.cs` + `DockerStreamDemux.cs` implementieren**

`src/Naudit.Infrastructure/Docker/IDockerClient.cs` — exakt der Interface-Block aus **Interfaces** oben, mit diesen XML-Kommentaren an den Membern:

```csharp
namespace Naudit.Infrastructure.Docker;

/// <summary>Dünne Naht über die Docker-Engine-API — nur die vom Session-Sandbox-Feature benötigten
/// Operationen. Impl: SocketDockerClient (Unix-Socket, kein NuGet); Tests nutzen FakeDockerClient.</summary>
public interface IDockerClient
{
    /// <summary>Socket erreichbar UND nutzbar (GET /_ping)? Liefert false statt zu werfen.</summary>
    Task<bool> PingAsync(CancellationToken ct = default);

    /// <summary>Image-Ref des Containers mit diesem Hostnamen (Selbst-Inspektion im Container:
    /// $HOSTNAME = Container-Id); null, wenn der Engine kein solcher Container bekannt ist.</summary>
    Task<string?> InspectSelfImageAsync(string hostname, CancellationToken ct = default);

    /// <summary>null = Container existiert nicht; sonst der Running-Zustand.</summary>
    Task<ContainerInfo?> InspectContainerAsync(string name, CancellationToken ct = default);

    /// <summary>docker run -d: create (409 = existiert schon, ok) + start, mit benanntem Volume.</summary>
    Task RunDetachedAsync(ContainerRunSpec spec, CancellationToken ct = default);

    Task StartAsync(string name, CancellationToken ct = default);
    Task StopAsync(string name, CancellationToken ct = default);
    Task RemoveContainerAsync(string name, CancellationToken ct = default);
    Task RemoveVolumeAsync(string name, CancellationToken ct = default);

    /// <summary>Schreibt eine Datei in den laufenden Container (PUT /archive, Ein-Eintrag-Tar,
    /// Mode 0644 — der Container-User ist non-root und muss sie lesen können).</summary>
    Task WriteFileAsync(string name, string directory, string fileName, string content, CancellationToken ct = default);

    /// <summary>docker exec (create + start, non-tty, ohne stdin): sammelt stdout/stderr aus dem
    /// multiplexten Stream und liefert den Exit-Code (GET /exec/{id}/json).</summary>
    Task<DockerExecResult> ExecAsync(string name, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string?>? environment, string workingDirectory, CancellationToken ct = default);

    /// <summary>Alle Container (auch gestoppte), deren Name mit dem Präfix beginnt — Adoption/Sweep/Status.</summary>
    Task<IReadOnlyList<ContainerListEntry>> ListContainersAsync(string namePrefix, CancellationToken ct = default);
}
```

(Records + Exception wie oben; `DockerUnavailableException` mit Kommentar `/// <summary>Docker-Plumbing-Fehler (Socket/API) — vom Runner fail-open behandelt, nie ein fachlicher claude-Fehler.</summary>`.)

`src/Naudit.Infrastructure/Docker/DockerStreamDemux.cs`:

```csharp
using System.Buffers.Binary;
using System.Text;

namespace Naudit.Infrastructure.Docker;

/// <summary>Trennt den multiplexten Docker-Attach-Stream (8-Byte-Header: Typ, 3×0, Big-Endian-Länge)
/// in stdout (Typ 1; Typ 0 wird stdout zugeschlagen) und stderr (Typ 2). Abrupt endende Streams
/// (Abriss mitten im Frame) liefern das bis dahin Angekommene statt zu werfen.</summary>
public static class DockerStreamDemux
{
    public static async Task<(string StdOut, string StdErr)> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        var header = new byte[8];
        while (true)
        {
            var read = await ReadUpToAsync(stream, header, ct);
            if (read < header.Length)
                break; // sauberes Ende (0) oder abgerissener Header — beides beendet den Stream
            var target = header[0] == 2 ? stderr : stdout;
            var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (length == 0)
                continue;
            var payload = new byte[length];
            var got = await ReadUpToAsync(stream, payload, ct);
            target.Write(payload, 0, got);
            if (got < payload.Length)
                break; // Abriss mitten im Payload
        }
        return (Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()));
    }

    // Liest bis der Puffer voll ist oder der Stream endet; liefert die tatsächlich gelesenen Bytes.
    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (n == 0)
                break;
            offset += n;
        }
        return offset;
    }
}
```

- [ ] **Step 4: Demux-Tests grün verifizieren**

Run: `dotnet test Naudit.slnx --filter DockerStreamDemuxTests 2>&1 | tail -3`
Expected: `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 5: `SocketDockerClient.cs` implementieren**

```csharp
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Naudit.Infrastructure.Docker;

/// <summary>IDockerClient über die Docker-Engine-HTTP-API am Unix-Socket. Bewusst ohne Docker.DotNet
/// (Supply-Chain-Härtung: kein neues NuGet) — SocketsHttpHandler mit ConnectCallback auf den
/// UnixDomainSocketEndPoint reicht für die wenigen benötigten Endpunkte. Transport-/API-Fehler
/// werden einheitlich als DockerUnavailableException gemeldet (Fail-Open-Naht des Runners).</summary>
public sealed class SocketDockerClient(string socketPath) : IDockerClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // Kein Client-Timeout: lange Execs (claude-Review) laufen bis zum CancellationToken des Aufrufers.
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    })
    { BaseAddress = new Uri("http://docker"), Timeout = Timeout.InfiniteTimeSpan };

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("/_ping", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false; // Ping ist die Sonde — nicht erreichbar ist ein Ergebnis, kein Fehler
        }
    }

    public async Task<string?> InspectSelfImageAsync(string hostname, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/containers/{hostname}/json"), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureAsync(resp, ct);
        return (await ReadJsonAsync<InspectResponse>(resp, ct)).Image;
    }

    public async Task<ContainerInfo?> InspectContainerAsync(string name, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/containers/{name}/json"), ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureAsync(resp, ct);
        var body = await ReadJsonAsync<InspectResponse>(resp, ct);
        return new ContainerInfo(body.State?.Running ?? false);
    }

    public async Task RunDetachedAsync(ContainerRunSpec spec, CancellationToken ct = default)
    {
        var create = new
        {
            Image = spec.Image,
            Cmd = spec.Command,
            HostConfig = new { Binds = new[] { $"{spec.VolumeName}:{spec.VolumeTarget}" } },
        };
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post,
            $"/containers/create?name={Uri.EscapeDataString(spec.Name)}")
        { Content = JsonContent.Create(create, options: JsonOpts) }, ct);
        // 409 = Name existiert bereits (Race zweier EnsureRunning) — dann genügt der Start.
        await EnsureAsync(resp, ct, HttpStatusCode.Conflict);
        await StartAsync(spec.Name, ct);
    }

    public async Task StartAsync(string name, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/containers/{name}/start"), ct);
        await EnsureAsync(resp, ct, HttpStatusCode.NotModified); // 304 = lief schon
    }

    public async Task StopAsync(string name, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/containers/{name}/stop?t=5"), ct);
        await EnsureAsync(resp, ct, HttpStatusCode.NotModified, HttpStatusCode.NotFound); // stand schon / weg
    }

    public async Task RemoveContainerAsync(string name, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/containers/{name}?force=true"), ct);
        await EnsureAsync(resp, ct, HttpStatusCode.NotFound);
    }

    public async Task RemoveVolumeAsync(string name, CancellationToken ct = default)
    {
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/volumes/{Uri.EscapeDataString(name)}"), ct);
        await EnsureAsync(resp, ct, HttpStatusCode.NotFound);
    }

    public async Task WriteFileAsync(string name, string directory, string fileName, string content, CancellationToken ct = default)
    {
        using var tarStream = new MemoryStream();
        await using (var tar = new TarWriter(tarStream, leaveOpen: true))
        {
            // 0644 statt 0600: der Tar-Eintrag gehört root, lesen muss ihn aber der non-root
            // Container-User (app). Inhalt ist der (bereits redigierte) Review-Prompt.
            var entry = new PaxTarEntry(TarEntryType.RegularFile, fileName)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            };
            await tar.WriteEntryAsync(entry, ct);
        }
        tarStream.Position = 0;
        using var req = new HttpRequestMessage(HttpMethod.Put,
            $"/containers/{name}/archive?path={Uri.EscapeDataString(directory)}")
        { Content = new StreamContent(tarStream) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
        using var resp = await SendAsync(req, ct);
        await EnsureAsync(resp, ct);
    }

    public async Task<DockerExecResult> ExecAsync(string name, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string?>? environment, string workingDirectory, CancellationToken ct = default)
    {
        var create = new
        {
            AttachStdin = false,
            AttachStdout = true,
            AttachStderr = true,
            Tty = false,
            Env = environment?.Select(kv => $"{kv.Key}={kv.Value}").ToArray() ?? [],
            Cmd = argv,
            WorkingDir = workingDirectory,
        };
        using var createResp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/containers/{name}/exec")
        { Content = JsonContent.Create(create, options: JsonOpts) }, ct);
        await EnsureAsync(createResp, ct);
        var execId = (await ReadJsonAsync<ExecCreateResponse>(createResp, ct)).Id;

        using var startReq = new HttpRequestMessage(HttpMethod.Post, $"/exec/{execId}/start")
        { Content = JsonContent.Create(new { Detach = false, Tty = false }, options: JsonOpts) };
        // ResponseHeadersRead: der Body IST der (multiplexte) stdout/stderr-Stream des Execs.
        using var startResp = await SendAsync(startReq, ct, HttpCompletionOption.ResponseHeadersRead);
        await EnsureAsync(startResp, ct);
        string stdout, stderr;
        try
        {
            await using var stream = await startResp.Content.ReadAsStreamAsync(ct);
            (stdout, stderr) = await DockerStreamDemux.ReadAsync(stream, ct);
        }
        catch (IOException ex)
        {
            throw new DockerUnavailableException($"Docker-Exec-Stream abgerissen: {ex.Message}", ex);
        }

        using var inspectResp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/exec/{execId}/json"), ct);
        await EnsureAsync(inspectResp, ct);
        var inspect = await ReadJsonAsync<ExecInspectResponse>(inspectResp, ct);
        return new DockerExecResult(inspect.ExitCode, stdout, stderr);
    }

    public async Task<IReadOnlyList<ContainerListEntry>> ListContainersAsync(string namePrefix, CancellationToken ct = default)
    {
        // Docker-name-Filter matcht Substrings — Präfix wird darum unten client-seitig nachgeprüft.
        var filters = Uri.EscapeDataString($"{{\"name\":[\"{namePrefix}\"]}}");
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"/containers/json?all=true&filters={filters}"), ct);
        await EnsureAsync(resp, ct);
        var entries = await ReadJsonAsync<List<ListEntry>>(resp, ct);
        return entries
            .Select(e => new ContainerListEntry(
                (e.Names?.FirstOrDefault() ?? "").TrimStart('/'),
                string.Equals(e.State, "running", StringComparison.OrdinalIgnoreCase)))
            .Where(e => e.Name.StartsWith(namePrefix, StringComparison.Ordinal))
            .ToList();
    }

    public void Dispose() => _http.Dispose();

    // Transportfehler (Socket weg, Verbindung verweigert, …) einheitlich als DockerUnavailableException.
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            return await _http.SendAsync(req, completion, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            throw new DockerUnavailableException($"Docker-Socket '{socketPath}' nicht nutzbar: {ex.Message}", ex);
        }
    }

    // API-Fehler (außer explizit akzeptierten Status) ebenfalls als DockerUnavailableException.
    private static async Task EnsureAsync(HttpResponseMessage resp, CancellationToken ct, params HttpStatusCode[] accepted)
    {
        if (resp.IsSuccessStatusCode || accepted.Contains(resp.StatusCode))
            return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        throw new DockerUnavailableException(
            $"Docker-API {(int)resp.StatusCode} bei {resp.RequestMessage?.RequestUri?.PathAndQuery}: {body}");
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken ct)
        => await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct)
           ?? throw new DockerUnavailableException($"Docker-API lieferte kein parsebares JSON ({typeof(T).Name}).");

    // Nur die benötigten Felder der Engine-Antworten.
    private sealed record InspectResponse(string? Image, InspectState? State);
    private sealed record InspectState(bool Running);
    private sealed record ListEntry(string[]? Names, string? State);
    private sealed record ExecCreateResponse(string Id);
    private sealed record ExecInspectResponse(int ExitCode, bool Running);
}
```

- [ ] **Step 6: Opt-in-Integrationstest schreiben**

`tests/Naudit.Tests/SocketDockerClientTests.cs`:

```csharp
using Naudit.Infrastructure.Docker;
using Xunit;

namespace Naudit.Tests;

/// <summary>Opt-in-Integrationstest gegen ein echtes Docker (Muster NauditDbContextPostgresTests):
/// läuft nur mit NAUDIT_TEST_DOCKER=1 (und lokal vorhandenem Image, Default busybox:latest —
/// vorher `docker pull busybox` ausführen). Lokal:
///   NAUDIT_TEST_DOCKER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SocketDockerClientTests
/// </summary>
public class SocketDockerClientTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("NAUDIT_TEST_DOCKER") == "1";
    private static string Image => Environment.GetEnvironmentVariable("NAUDIT_TEST_DOCKER_IMAGE") ?? "busybox:latest";
    private static string SocketPath => Environment.GetEnvironmentVariable("NAUDIT_TEST_DOCKER_SOCKET") ?? "/var/run/docker.sock";

    [Fact]
    public async Task FullLifecycle_run_exec_stop_start_remove()
    {
        if (!Enabled) return; // ohne Docker-Env: übersprungen

        using var docker = new SocketDockerClient(SocketPath);
        Assert.True(await docker.PingAsync());

        var name = $"naudit-test-{Guid.NewGuid():N}";
        try
        {
            Assert.Null(await docker.InspectContainerAsync(name));

            await docker.RunDetachedAsync(new ContainerRunSpec(name, Image, name, "/data", ["sleep", "300"]));
            Assert.True((await docker.InspectContainerAsync(name))!.Running);

            await docker.WriteFileAsync(name, "/tmp", "stdin-test", "hello sandbox");
            var cat = await docker.ExecAsync(name,
                ["/bin/sh", "-c", "exec \"$0\" \"$@\" < /tmp/stdin-test", "cat"], null, "/tmp");
            Assert.Equal(0, cat.ExitCode);
            Assert.Equal("hello sandbox", cat.StdOut);

            var env = await docker.ExecAsync(name, ["/bin/sh", "-c", "printf %s \"$NAUDIT_T\""],
                new Dictionary<string, string?> { ["NAUDIT_T"] = "42" }, "/tmp");
            Assert.Equal("42", env.StdOut);

            var fail = await docker.ExecAsync(name, ["/bin/sh", "-c", "echo boom >&2; exit 3"], null, "/tmp");
            Assert.Equal(3, fail.ExitCode);
            Assert.Contains("boom", fail.StdErr);

            var listed = await docker.ListContainersAsync("naudit-test-");
            Assert.Contains(listed, e => e.Name == name && e.Running);

            await docker.StopAsync(name);
            Assert.False((await docker.InspectContainerAsync(name))!.Running);
            await docker.StartAsync(name);
            Assert.True((await docker.InspectContainerAsync(name))!.Running);
        }
        finally
        {
            await docker.RemoveContainerAsync(name);
            await docker.RemoveVolumeAsync(name);
        }
        Assert.Null(await docker.InspectContainerAsync(name));
    }

    [Fact]
    public async Task Ping_missingSocket_isFalse_notThrow()
    {
        if (!Enabled) return;
        using var docker = new SocketDockerClient("/nonexistent/docker.sock");
        Assert.False(await docker.PingAsync());
    }
}
```

- [ ] **Step 7: Build + Gesamtsuite grün; Integrationstest falls Docker lokal verfügbar**

Run: `dotnet test Naudit.slnx 2>&1 | tail -3`
Expected: `Passed!` (Docker-Integrationstests returnen leer ohne Env).

Run (nur falls `docker ps` auf der Maschine funktioniert; sonst Schritt überspringen und im Report vermerken):
`docker pull busybox >/dev/null 2>&1; NAUDIT_TEST_DOCKER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SocketDockerClientTests 2>&1 | tail -3`
Expected: `Passed! - Failed: 0, Passed: 2`

- [ ] **Step 8: Commit**

```bash
git add src/Naudit.Infrastructure/Docker/ tests/Naudit.Tests/DockerStreamDemuxTests.cs tests/Naudit.Tests/SocketDockerClientTests.cs
git commit -m "feat(sandbox): IDockerClient-Naht + SocketDockerClient (Engine-API am Unix-Socket, kein NuGet)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: `SessionContainerManager` (Lifecycle: EnsureRunning, LRU-Cap, Locks, Sweep, Adoption, Remove)

**Files:**
- Create: `src/Naudit.Infrastructure/Ai/Sandbox/SessionContainerManager.cs`
- Create: `tests/Naudit.Tests/Fakes/FakeDockerClient.cs`
- Create: `tests/Naudit.Tests/Fakes/FakeTime.cs`
- Test: `tests/Naudit.Tests/SessionContainerManagerTests.cs`

**Interfaces:**
- Consumes: `IDockerClient` + Records + `DockerUnavailableException` (Task 2), `SessionSandboxOptions` (Task 1).
- Produces (`Naudit.Infrastructure.Ai.Sandbox`): `SessionContainerManager` mit
  `const string NamePrefix = "naudit-session-"`, `const string VolumeTarget = "/home/app"`,
  `static string ContainerName(int accountId)`,
  `Task<string> EnsureRunningAsync(int accountId, CancellationToken ct = default)`,
  `Task<IDisposable> AcquireLockAsync(int accountId, CancellationToken ct = default)`,
  `void Touch(int accountId)`, `DateTimeOffset? LastUsed(int accountId)`,
  `Task SweepIdleAsync(CancellationToken ct = default)`, `Task AdoptExistingAsync(CancellationToken ct = default)`,
  `Task RemoveAsync(int accountId, CancellationToken ct = default)`, `Task<int> CountRunningAsync(CancellationToken ct = default)`.
  Ctor: `(IDockerClient docker, SessionSandboxOptions options, ILogger<SessionContainerManager> logger, TimeProvider? time = null)`.

- [ ] **Step 1: Fakes schreiben**

`tests/Naudit.Tests/Fakes/FakeTime.cs`:

```csharp
namespace Naudit.Tests.Fakes;

// Stellbare Uhr für Idle-/LRU-Tests (TimeProvider ist in-box, kein Testing-NuGet nötig).
internal sealed class FakeTime : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => UtcNow;
}
```

`tests/Naudit.Tests/Fakes/FakeDockerClient.cs`:

```csharp
using Naudit.Infrastructure.Docker;

namespace Naudit.Tests.Fakes;

/// <summary>Skriptbarer IDockerClient: Container/Volumes in-memory, zeichnet Aufrufe auf und kann
/// gezielt DockerUnavailableException werfen (FailNextExecs) oder Execs verzögern (ExecDelay).</summary>
internal sealed class FakeDockerClient : IDockerClient
{
    public Dictionary<string, bool> Containers { get; } = new();   // Name -> Running
    public HashSet<string> Volumes { get; } = new();
    public List<string> Calls { get; } = new();
    public List<(string Container, string Path, string Content)> WrittenFiles { get; } = new();
    public List<(string Container, IReadOnlyList<string> Argv, IReadOnlyDictionary<string, string?>? Env, string WorkingDir)> Execs { get; } = new();
    public Queue<DockerExecResult> ExecResults { get; } = new();   // leer ⇒ Exit 0, ""/""
    public bool PingResult { get; set; } = true;
    public string? SelfImage { get; set; } = "sha256:self-image";
    public int FailNextExecs { get; set; }
    public TimeSpan? ExecDelay { get; set; }
    public ContainerRunSpec? LastRunSpec { get; private set; }

    public Task<bool> PingAsync(CancellationToken ct = default)
    {
        Calls.Add("ping");
        return Task.FromResult(PingResult);
    }

    public Task<string?> InspectSelfImageAsync(string hostname, CancellationToken ct = default)
    {
        Calls.Add($"inspect-self:{hostname}");
        return Task.FromResult(SelfImage);
    }

    public Task<ContainerInfo?> InspectContainerAsync(string name, CancellationToken ct = default)
        => Task.FromResult(Containers.TryGetValue(name, out var running) ? new ContainerInfo(running) : null);

    public Task RunDetachedAsync(ContainerRunSpec spec, CancellationToken ct = default)
    {
        Calls.Add($"run:{spec.Name}");
        LastRunSpec = spec;
        Containers[spec.Name] = true;
        Volumes.Add(spec.VolumeName);
        return Task.CompletedTask;
    }

    public Task StartAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"start:{name}");
        Containers[name] = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"stop:{name}");
        if (Containers.ContainsKey(name)) Containers[name] = false;
        return Task.CompletedTask;
    }

    public Task RemoveContainerAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"rm:{name}");
        Containers.Remove(name);
        return Task.CompletedTask;
    }

    public Task RemoveVolumeAsync(string name, CancellationToken ct = default)
    {
        Calls.Add($"rmvol:{name}");
        Volumes.Remove(name);
        return Task.CompletedTask;
    }

    public Task WriteFileAsync(string name, string directory, string fileName, string content, CancellationToken ct = default)
    {
        WrittenFiles.Add((name, $"{directory}/{fileName}", content));
        return Task.CompletedTask;
    }

    public async Task<DockerExecResult> ExecAsync(string name, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string?>? environment, string workingDirectory, CancellationToken ct = default)
    {
        Execs.Add((name, argv, environment, workingDirectory));
        if (FailNextExecs > 0)
        {
            FailNextExecs--;
            throw new DockerUnavailableException("fake: exec down");
        }
        if (ExecDelay is { } delay)
            await Task.Delay(delay, ct);
        return ExecResults.Count > 0 ? ExecResults.Dequeue() : new DockerExecResult(0, "", "");
    }

    public Task<IReadOnlyList<ContainerListEntry>> ListContainersAsync(string namePrefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ContainerListEntry>>(Containers
            .Where(c => c.Key.StartsWith(namePrefix, StringComparison.Ordinal))
            .Select(c => new ContainerListEntry(c.Key, c.Value))
            .ToList());
}
```

- [ ] **Step 2: Failing Tests schreiben**

`tests/Naudit.Tests/SessionContainerManagerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class SessionContainerManagerTests
{
    private static SessionContainerManager Create(FakeDockerClient docker, FakeTime? time = null,
        SessionSandboxOptions? options = null)
        => new(docker, options ?? new SessionSandboxOptions(),
            NullLogger<SessionContainerManager>.Instance, time ?? new FakeTime());

    [Fact]
    public async Task EnsureRunning_missing_runsDetached_withVolumeAndSleep()
    {
        var docker = new FakeDockerClient();
        var manager = Create(docker);

        var name = await manager.EnsureRunningAsync(7);

        Assert.Equal("naudit-session-7", name);
        Assert.Equal(["run:naudit-session-7"], docker.Calls.Where(c => c.StartsWith("run:")));
        Assert.Equal("sha256:self-image", docker.LastRunSpec!.Image);
        Assert.Equal("naudit-session-7", docker.LastRunSpec.VolumeName);
        Assert.Equal("/home/app", docker.LastRunSpec.VolumeTarget);
        Assert.Equal(["sleep", "infinity"], docker.LastRunSpec.Command);
        Assert.NotNull(manager.LastUsed(7)); // EnsureRunning zählt als Nutzung
    }

    [Fact]
    public async Task EnsureRunning_imageOverride_skipsSelfInspection()
    {
        var docker = new FakeDockerClient { SelfImage = null };
        var manager = Create(docker, options: new SessionSandboxOptions { Image = "ghcr.io/x/naudit:v9" });

        await manager.EnsureRunningAsync(1);

        Assert.Equal("ghcr.io/x/naudit:v9", docker.LastRunSpec!.Image);
        Assert.DoesNotContain(docker.Calls, c => c.StartsWith("inspect-self:"));
    }

    [Fact]
    public async Task EnsureRunning_noImageResolvable_throwsDockerUnavailable()
    {
        var docker = new FakeDockerClient { SelfImage = null }; // nicht im Container, kein Override
        var manager = Create(docker);

        await Assert.ThrowsAsync<Naudit.Infrastructure.Docker.DockerUnavailableException>(
            () => manager.EnsureRunningAsync(1));
    }

    [Fact]
    public async Task EnsureRunning_exited_startsWithoutCreate()
    {
        var docker = new FakeDockerClient { Containers = { ["naudit-session-3"] = false } };
        var manager = Create(docker);

        await manager.EnsureRunningAsync(3);

        Assert.Contains("start:naudit-session-3", docker.Calls);
        Assert.DoesNotContain(docker.Calls, c => c.StartsWith("run:"));
    }

    [Fact]
    public async Task EnsureRunning_running_isNoop()
    {
        var docker = new FakeDockerClient { Containers = { ["naudit-session-3"] = true } };
        var manager = Create(docker);

        await manager.EnsureRunningAsync(3);

        Assert.DoesNotContain(docker.Calls, c => c.StartsWith("run:") || c.StartsWith("start:"));
    }

    [Fact]
    public async Task EnsureRunning_capReached_stopsLeastRecentlyUsed()
    {
        var docker = new FakeDockerClient();
        var time = new FakeTime();
        var manager = Create(docker, time, new SessionSandboxOptions { MaxLiveContainers = 2 });

        await manager.EnsureRunningAsync(1);
        time.UtcNow = time.UtcNow.AddMinutes(1);
        await manager.EnsureRunningAsync(2);
        time.UtcNow = time.UtcNow.AddMinutes(1);
        await manager.EnsureRunningAsync(3); // Cap 2 erreicht ⇒ LRU (Konto 1) wird gestoppt

        Assert.Contains("stop:naudit-session-1", docker.Calls);
        Assert.False(docker.Containers["naudit-session-1"]);
        Assert.True(docker.Containers["naudit-session-2"]);
        Assert.True(docker.Containers["naudit-session-3"]);
    }

    [Fact]
    public async Task AcquireLock_serializesPerAccount_butNotAcrossAccounts()
    {
        var docker = new FakeDockerClient();
        var manager = Create(docker);

        var first = await manager.AcquireLockAsync(5);
        var secondTask = manager.AcquireLockAsync(5);
        var otherAccount = await manager.AcquireLockAsync(6); // fremdes Konto blockiert nicht

        Assert.False(secondTask.IsCompleted);
        first.Dispose();
        (await secondTask).Dispose();
        otherAccount.Dispose();
    }

    [Fact]
    public async Task Sweep_stopsOnlyIdleRunningContainers()
    {
        var docker = new FakeDockerClient();
        var time = new FakeTime();
        var manager = Create(docker, time, new SessionSandboxOptions { IdleTimeout = TimeSpan.FromHours(1) });

        await manager.EnsureRunningAsync(1);
        time.UtcNow = time.UtcNow.AddMinutes(90); // Konto 1 ist jetzt idle
        await manager.EnsureRunningAsync(2);       // Konto 2 frisch

        await manager.SweepIdleAsync();

        Assert.Contains("stop:naudit-session-1", docker.Calls);
        Assert.DoesNotContain("stop:naudit-session-2", docker.Calls);
    }

    [Fact]
    public async Task Adopt_registersOnlyParsablePrefixedNames()
    {
        var docker = new FakeDockerClient
        {
            Containers =
            {
                ["naudit-session-9"] = true,
                ["naudit-session-x"] = true,  // nicht parsebar ⇒ ignoriert
                ["someone-elses-container"] = true,
            },
        };
        var manager = Create(docker);

        await manager.AdoptExistingAsync();

        Assert.NotNull(manager.LastUsed(9));
        // Frisch adoptiert zählt als "gerade genutzt": ein sofortiger Sweep stoppt nichts.
        await manager.SweepIdleAsync();
        Assert.DoesNotContain(docker.Calls, c => c.StartsWith("stop:"));
    }

    [Fact]
    public async Task Remove_stopsRemovesContainerAndVolume()
    {
        var docker = new FakeDockerClient { Containers = { ["naudit-session-4"] = true } };
        docker.Volumes.Add("naudit-session-4");
        var manager = Create(docker);

        await manager.RemoveAsync(4);

        Assert.Equal(["stop:naudit-session-4", "rm:naudit-session-4", "rmvol:naudit-session-4"],
            docker.Calls.Where(c => !c.StartsWith("ping")));
        Assert.Empty(docker.Containers);
        Assert.Empty(docker.Volumes);
        Assert.Null(manager.LastUsed(4));
    }

    [Fact]
    public async Task CountRunning_countsOnlyRunningPrefixed()
    {
        var docker = new FakeDockerClient
        {
            Containers = { ["naudit-session-1"] = true, ["naudit-session-2"] = false, ["other"] = true },
        };
        var manager = Create(docker);

        Assert.Equal(1, await manager.CountRunningAsync());
    }
}
```

- [ ] **Step 3: Rot verifizieren**

Run: `dotnet test Naudit.slnx --filter SessionContainerManagerTests 2>&1 | tail -5`
Expected: Compile-Fehler (Manager fehlt).

- [ ] **Step 4: `SessionContainerManager.cs` implementieren**

```csharp
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Docker;

namespace Naudit.Infrastructure.Ai.Sandbox;

/// <summary>Lifecycle der Account-Session-Container: ein langlebiger Container + benanntes Volume
/// pro Account ("naudit-session-&lt;accountId&gt;"), Reviews laufen per docker exec hinein.
/// Prozessweiter Singleton: hält Locks (nie zwei Execs im selben Container — Race auf den
/// CLI-Credential-Cache im Volume) und LastUsed (Idle-Sweep, LRU-Cap) in-memory; nach einem
/// Naudit-Neustart adoptiert er bestehende Container über das Namens-Präfix.</summary>
public sealed class SessionContainerManager(
    IDockerClient docker,
    SessionSandboxOptions options,
    ILogger<SessionContainerManager> logger,
    TimeProvider? time = null)
{
    public const string NamePrefix = "naudit-session-";

    /// <summary>Mount-Ziel des Account-Volumes = HOME des Images — dort hält die claude-CLI ihre
    /// Credentials, das Volume macht die Session über Container-Stop UND -Remove hinweg warm.</summary>
    public const string VolumeTarget = "/home/app";

    private readonly object _gate = new();
    private readonly Dictionary<int, SemaphoreSlim> _locks = [];
    private readonly Dictionary<int, DateTimeOffset> _lastUsed = [];
    private string? _image;

    public static string ContainerName(int accountId) => $"{NamePrefix}{accountId}";

    /// <summary>Container existiert + läuft; legt fehlende an (Image = Override oder
    /// Selbst-Inspektion), startet gestoppte, erzwingt vorher das LRU-Cap. Liefert den Namen.</summary>
    public async Task<string> EnsureRunningAsync(int accountId, CancellationToken ct = default)
    {
        var name = ContainerName(accountId);
        var info = await docker.InspectContainerAsync(name, ct);
        if (info is null)
        {
            await EnforceCapAsync(accountId, ct);
            var image = await ResolveImageAsync(ct);
            logger.LogInformation("Session-Sandbox: lege Container {Name} an (Image {Image}).", name, image);
            await docker.RunDetachedAsync(
                new ContainerRunSpec(name, image, VolumeName: name, VolumeTarget, ["sleep", "infinity"]), ct);
        }
        else if (!info.Running)
        {
            await EnforceCapAsync(accountId, ct);
            await docker.StartAsync(name, ct);
        }
        Touch(accountId); // deckt auch die Laufzeit des folgenden Execs ab (Sweep-Schutz)
        return name;
    }

    /// <summary>Exklusiv-Lock pro Account — nie zwei Execs gleichzeitig im selben Container.</summary>
    public async Task<IDisposable> AcquireLockAsync(int accountId, CancellationToken ct = default)
    {
        SemaphoreSlim sem;
        lock (_gate)
        {
            if (!_locks.TryGetValue(accountId, out sem!))
                _locks[accountId] = sem = new SemaphoreSlim(1, 1);
        }
        await sem.WaitAsync(ct);
        return new Releaser(sem);
    }

    public void Touch(int accountId)
    {
        lock (_gate) _lastUsed[accountId] = Now();
    }

    public DateTimeOffset? LastUsed(int accountId)
    {
        lock (_gate) return _lastUsed.TryGetValue(accountId, out var t) ? t : null;
    }

    /// <summary>Stoppt laufende Container, deren Account länger als IdleTimeout still ist
    /// (stop, nie rm — die warme Session liegt im Volume).</summary>
    public async Task SweepIdleAsync(CancellationToken ct = default)
    {
        var cutoff = Now() - options.IdleTimeout;
        foreach (var entry in await docker.ListContainersAsync(NamePrefix, ct))
        {
            if (!entry.Running || !TryParseAccountId(entry.Name, out var accountId))
                continue;
            var last = LastUsed(accountId) ?? DateTimeOffset.MinValue;
            if (last > cutoff)
                continue;
            logger.LogInformation("Session-Sandbox: stoppe idle Container {Name} (zuletzt genutzt {Last:u}).",
                entry.Name, last);
            await docker.StopAsync(entry.Name, ct);
        }
    }

    /// <summary>Nach Naudit-Neustart: bestehende Präfix-Container als "gerade genutzt" übernehmen
    /// (der Restart-Loop in Program.cs betrifft die Geschwister-Container nicht).</summary>
    public async Task AdoptExistingAsync(CancellationToken ct = default)
    {
        var entries = await docker.ListContainersAsync(NamePrefix, ct);
        var adopted = 0;
        lock (_gate)
        {
            foreach (var entry in entries)
            {
                if (!TryParseAccountId(entry.Name, out var accountId) || _lastUsed.ContainsKey(accountId))
                    continue;
                _lastUsed[accountId] = Now();
                adopted++;
            }
        }
        if (adopted > 0)
            logger.LogInformation("Session-Sandbox: {Count} bestehende Container adoptiert.", adopted);
    }

    /// <summary>Pool-Austritt/Token-Löschung: Container UND Volume entfernen — das Volume
    /// enthält die CLI-Credentials des Accounts und darf ihn nicht überleben.</summary>
    public async Task RemoveAsync(int accountId, CancellationToken ct = default)
    {
        var name = ContainerName(accountId);
        using var _ = await AcquireLockAsync(accountId, ct); // nie parallel zu einem laufenden Exec
        await docker.StopAsync(name, ct);
        await docker.RemoveContainerAsync(name, ct);
        await docker.RemoveVolumeAsync(name, ct);
        lock (_gate) _lastUsed.Remove(accountId);
    }

    public async Task<int> CountRunningAsync(CancellationToken ct = default)
        => (await docker.ListContainersAsync(NamePrefix, ct)).Count(e => e.Running);

    // Vor dem Start eines weiteren Containers Platz schaffen: läuft bereits das Cap (ohne den
    // startenden Account selbst), wird der am längsten ungenutzte gestoppt (LRU; unbekannt = ältester).
    private async Task EnforceCapAsync(int startingAccountId, CancellationToken ct)
    {
        var cap = Math.Max(1, options.MaxLiveContainers);
        var running = (await docker.ListContainersAsync(NamePrefix, ct))
            .Where(e => e.Running && TryParseAccountId(e.Name, out var id) && id != startingAccountId)
            .ToList();
        var excess = running.Count - (cap - 1);
        if (excess <= 0)
            return;
        var victims = running
            .OrderBy(e => TryParseAccountId(e.Name, out var id) ? LastUsed(id) ?? DateTimeOffset.MinValue : DateTimeOffset.MinValue)
            .Take(excess);
        foreach (var victim in victims)
        {
            logger.LogInformation("Session-Sandbox: MaxLiveContainers ({Cap}) erreicht — stoppe LRU-Container {Name}.",
                cap, victim.Name);
            await docker.StopAsync(victim.Name, ct);
        }
    }

    private async Task<string> ResolveImageAsync(CancellationToken ct)
    {
        if (_image is not null)
            return _image;
        if (!string.IsNullOrWhiteSpace(options.Image))
            return _image = options.Image;
        // Selbst-Inspektion: im Container ist $HOSTNAME die eigene Container-Id ⇒ Image driftet
        // nie von der laufenden Naudit-Version weg. Außerhalb von Docker gibt es kein Image.
        var self = await docker.InspectSelfImageAsync(Environment.MachineName, ct);
        return _image = self ?? throw new DockerUnavailableException(
            "Eigenes Image nicht ermittelbar (läuft Naudit außerhalb von Docker?) — Naudit:Ai:Sandbox:Image setzen.");
    }

    private DateTimeOffset Now() => (time ?? TimeProvider.System).GetUtcNow();

    private static bool TryParseAccountId(string name, out int accountId)
    {
        accountId = 0;
        return name.StartsWith(NamePrefix, StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(NamePrefix.Length), out accountId);
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                sem.Release();
        }
    }
}
```

- [ ] **Step 5: Grün verifizieren**

Run: `dotnet test Naudit.slnx --filter SessionContainerManagerTests 2>&1 | tail -3`
Expected: `Passed! - Failed: 0, Passed: 11`

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Ai/Sandbox/SessionContainerManager.cs tests/Naudit.Tests/Fakes/FakeDockerClient.cs tests/Naudit.Tests/Fakes/FakeTime.cs tests/Naudit.Tests/SessionContainerManagerTests.cs
git commit -m "feat(sandbox): SessionContainerManager — EnsureRunning/LRU-Cap/Locks/Sweep/Adoption/Remove

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: `DockerSessionRunner` + `ISessionRunnerFactory`-Naht + DI-Verdrahtung

**Files:**
- Create: `src/Naudit.Infrastructure/Ai/Sandbox/SessionSandboxState.cs`
- Create: `src/Naudit.Infrastructure/Ai/Sandbox/ISessionRunnerFactory.cs`
- Create: `src/Naudit.Infrastructure/Ai/Sandbox/DockerSessionRunner.cs`
- Modify: `src/Naudit.Infrastructure/Ai/ClaudeCode/SessionSelectionFactory.cs` (Ctor-Param `IProcessRunner runner` → `ISessionRunnerFactory runnerFactory`)
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (Sandbox-Registrierung vor `services.AddSingleton<SessionSelectionFactory>();`, Zeile ~92)
- Modify: `tests/Naudit.Tests/AuthorSessionRouterTests.cs` (Zeile ~42) und `tests/Naudit.Tests/RoundRobinSessionRouterTests.cs` (Zeile ~35): `new SessionSelectionFactory(..., runner, ...)` → `new SessionSelectionFactory(..., new InProcessSessionRunnerFactory(runner), ...)` (Using `Naudit.Infrastructure.Ai.Sandbox` ergänzen; den vorhandenen `StubProcessRunner` unverändert weiterreichen)
- Test: `tests/Naudit.Tests/DockerSessionRunnerTests.cs`
- Test: `tests/Naudit.Tests/SandboxWiringTests.cs`

**Interfaces:**
- Consumes: `SessionContainerManager` (Task 3), `IDockerClient`/`DockerUnavailableException`/`DockerExecResult` (Task 2), `IProcessRunner`/`ProcessSpec`/`ProcessResult` (bestehend), `SessionSandbox`-Enum (Task 1).
- Produces:

```csharp
public sealed class SessionSandboxState        // Singleton, immer registriert
{
    public bool? SocketReachable { get; }       // null = noch nie gepingt
    public void ReportPing(bool ok);
}

public interface ISessionRunnerFactory { IProcessRunner ForAccount(int accountId); }
public sealed class InProcessSessionRunnerFactory(IProcessRunner runner) : ISessionRunnerFactory;
public sealed class DockerSessionRunnerFactory(SessionContainerManager manager, IDockerClient docker,
    IProcessRunner inProcess, SessionSandboxState state, ILoggerFactory loggerFactory) : ISessionRunnerFactory;
public sealed class DockerSessionRunner(...) : IProcessRunner;  // account-gebunden
```

- Task 5 konsumiert `SessionSandboxState.ReportPing`; Task 7 liest `SocketReachable`.

- [ ] **Step 1: Failing Runner-Tests schreiben**

`tests/Naudit.Tests/DockerSessionRunnerTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Infrastructure.Docker;
using Naudit.Infrastructure.Process;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class DockerSessionRunnerTests
{
    private static ProcessSpec Spec(string? stdIn = "DIFF", TimeSpan? timeout = null) => new(
        FileName: "claude",
        Arguments: ["-p", "--output-format", "json"],
        StdIn: stdIn,
        Environment: new Dictionary<string, string?>
        {
            ["CLAUDE_CONFIG_DIR"] = "/tmp/host-only",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "tok-123",
        },
        WorkingDirectory: "/tmp",
        Timeout: timeout ?? TimeSpan.FromMinutes(5));

    private static (DockerSessionRunner Runner, FakeDockerClient Docker, StubProcessRunner Fallback, SessionSandboxState State, SessionContainerManager Manager)
        Create(FakeDockerClient? docker = null)
    {
        docker ??= new FakeDockerClient();
        var manager = new SessionContainerManager(docker, new SessionSandboxOptions(),
            NullLogger<SessionContainerManager>.Instance, new FakeTime());
        var fallback = new StubProcessRunner(_ => new ProcessResult(0, "fallback-out", ""));
        var state = new SessionSandboxState();
        var runner = new DockerSessionRunner(42, manager, docker, fallback, state,
            NullLogger<DockerSessionRunner>.Instance);
        return (runner, docker, fallback, state, manager);
    }

    [Fact]
    public async Task Run_execsInContainer_withShRedirect_andTokenOnlyEnv()
    {
        var (runner, docker, fallback, _, _) = Create();
        docker.ExecResults.Enqueue(new DockerExecResult(0, "{\"ok\":true}", ""));

        var result = await runner.RunAsync(Spec());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("{\"ok\":true}", result.StdOut);
        Assert.Empty(fallback.Specs); // kein In-Process-Lauf

        var written = Assert.Single(docker.WrittenFiles);
        Assert.Equal(("naudit-session-42", "/tmp/naudit-stdin", "DIFF"), written);

        var exec = Assert.Single(docker.Execs);
        Assert.Equal("naudit-session-42", exec.Container);
        Assert.Equal("/tmp", exec.WorkingDir);
        Assert.Equal(["/bin/sh", "-c", "exec \"$0\" \"$@\" < /tmp/naudit-stdin", "claude", "-p", "--output-format", "json"],
            exec.Argv);
        // Env-Filter: NUR der Token wandert mit, CLAUDE_CONFIG_DIR wird verworfen (Volume-HOME gewinnt).
        var env = Assert.Single(exec.Env!);
        Assert.Equal(("CLAUDE_CODE_OAUTH_TOKEN", "tok-123"), (env.Key, env.Value));
    }

    [Fact]
    public async Task Run_withoutStdin_execsArgvDirectly()
    {
        var (runner, docker, _, _, _) = Create();

        await runner.RunAsync(Spec(stdIn: null));

        Assert.Empty(docker.WrittenFiles);
        Assert.Equal(["claude", "-p", "--output-format", "json"], Assert.Single(docker.Execs).Argv);
    }

    [Fact]
    public async Task Run_containerVanishedMidExec_ensuresAndRetriesOnce()
    {
        var (runner, docker, fallback, _, _) = Create();
        docker.FailNextExecs = 1; // erster Exec scheitert (Container extern entfernt)
        docker.ExecResults.Enqueue(new DockerExecResult(0, "second-try", ""));

        var result = await runner.RunAsync(Spec());

        Assert.Equal("second-try", result.StdOut);
        Assert.Equal(2, docker.Execs.Count);
        Assert.Empty(fallback.Specs);
    }

    [Fact]
    public async Task Run_dockerBroken_fallsBackInProcess()
    {
        var (runner, docker, fallback, _, _) = Create();
        docker.FailNextExecs = 2; // Erstversuch UND Retry scheitern

        var result = await runner.RunAsync(Spec());

        Assert.Equal("fallback-out", result.StdOut);
        Assert.Single(fallback.Specs); // Original-Spec ging in-process weiter
    }

    [Fact]
    public async Task Run_knownUnreachable_skipsDockerEntirely()
    {
        var (runner, docker, fallback, state, _) = Create();
        state.ReportPing(false);

        var result = await runner.RunAsync(Spec());

        Assert.Equal("fallback-out", result.StdOut);
        Assert.Empty(docker.Execs);
        Assert.Empty(docker.Calls); // nicht mal ein Inspect
    }

    [Fact]
    public async Task Run_timeout_stopsContainer_andThrowsTimeout()
    {
        var (runner, docker, _, _, _) = Create();
        docker.ExecDelay = TimeSpan.FromSeconds(30);

        await Assert.ThrowsAsync<TimeoutException>(
            () => runner.RunAsync(Spec(timeout: TimeSpan.FromMilliseconds(100))));

        Assert.Contains("stop:naudit-session-42", docker.Calls); // Kill-Pfad: Stop beendet den Exec
    }

    [Fact]
    public async Task Run_externalCancel_rethrowsCancellation()
    {
        var (runner, docker, _, _, _) = Create();
        docker.ExecDelay = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync(Spec(), cts.Token));
    }

    [Fact]
    public async Task Run_success_touchesLastUsed()
    {
        var (runner, _, _, _, manager) = Create();

        await runner.RunAsync(Spec());

        Assert.NotNull(manager.LastUsed(42));
    }
}
```

- [ ] **Step 2: Rot verifizieren**

Run: `dotnet test Naudit.slnx --filter DockerSessionRunnerTests 2>&1 | tail -5`
Expected: Compile-Fehler.

- [ ] **Step 3: State, Factory-Naht und Runner implementieren**

`src/Naudit.Infrastructure/Ai/Sandbox/SessionSandboxState.cs`:

```csharp
namespace Naudit.Infrastructure.Ai.Sandbox;

/// <summary>Prozessweiter Sandbox-Zustand: letztes Ping-Ergebnis (null = noch nie geprüft).
/// Der Sweeper schreibt (Start + jeder Tick ⇒ Selbstheilung nach Socket-Ausfall), der Runner
/// liest (bekannt-unerreichbar ⇒ gar kein Docker-Versuch), der Status-Endpoint zeigt es an.</summary>
public sealed class SessionSandboxState
{
    private int _reachable = -1; // -1 unbekannt, 0 nein, 1 ja (Interlocked-tauglich)

    public bool? SocketReachable => _reachable switch { 1 => true, 0 => false, _ => null };

    public void ReportPing(bool ok) => Interlocked.Exchange(ref _reachable, ok ? 1 : 0);
}
```

`src/Naudit.Infrastructure/Ai/Sandbox/ISessionRunnerFactory.cs`:

```csharp
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Ai.Sandbox;

/// <summary>Wählt den IProcessRunner für einen Abo-Session-Lauf eines Accounts: in-process
/// (heutiges Verhalten) oder account-gebundener Docker-Container (SessionSandbox=Docker).
/// Die Naht sitzt in SessionSelectionFactory.ForAccount — SAST/git bleiben unberührt am
/// geteilten SystemProcessRunner.</summary>
public interface ISessionRunnerFactory
{
    IProcessRunner ForAccount(int accountId);
}

/// <summary>Default (SessionSandbox=None): immer der geteilte In-Process-Runner.</summary>
public sealed class InProcessSessionRunnerFactory(IProcessRunner runner) : ISessionRunnerFactory
{
    public IProcessRunner ForAccount(int accountId) => runner;
}
```

`src/Naudit.Infrastructure/Ai/Sandbox/DockerSessionRunner.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Docker;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Ai.Sandbox;

/// <summary>IProcessRunner, der den claude-Lauf per docker exec in den langlebigen Container des
/// Accounts verlegt (warme Session: Prozess-Container + Auth im Volume bleiben zwischen Reviews
/// bestehen). Fail-open: jeder Docker-Plumbing-Fehler (DockerUnavailableException) fällt auf den
/// In-Process-Runner zurück — ein Review scheitert nie an der Sandbox. Timeout/Cancel spiegeln
/// den SystemProcessRunner-Kill-Pfad: Container-Stop (beendet den Exec) + TimeoutException.</summary>
public sealed class DockerSessionRunner(
    int accountId,
    SessionContainerManager manager,
    IDockerClient docker,
    IProcessRunner inProcessFallback,
    SessionSandboxState state,
    ILogger logger) : IProcessRunner
{
    // Fester Pfad, pro Lauf überschrieben: der Account-Lock serialisiert Execs, und /tmp liegt in
    // der Container-Schicht — nicht im persistenten Session-Volume (kein Prompt-Ansammeln dort).
    private const string StdInDirectory = "/tmp";
    private const string StdInFileName = "naudit-stdin";

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
    {
        // Letzter Ping negativ ⇒ Docker gar nicht erst versuchen (der Sweeper pingt periodisch neu).
        if (state.SocketReachable == false)
            return await inProcessFallback.RunAsync(spec, ct);

        try
        {
            return await RunInContainerAsync(spec, ct);
        }
        catch (DockerUnavailableException ex)
        {
            logger.LogWarning(ex,
                "Session-Sandbox für Konto {AccountId} nicht verfügbar — Fallback auf In-Process-Lauf.",
                accountId);
            return await inProcessFallback.RunAsync(spec, ct);
        }
    }

    private async Task<ProcessResult> RunInContainerAsync(ProcessSpec spec, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(spec.Timeout);
        var name = SessionContainerManager.ContainerName(accountId);
        try
        {
            await manager.EnsureRunningAsync(accountId, timeoutCts.Token);
            using var _ = await manager.AcquireLockAsync(accountId, timeoutCts.Token);

            DockerExecResult result;
            try
            {
                result = await ExecOnceAsync(name, spec, timeoutCts.Token);
            }
            catch (DockerUnavailableException)
            {
                // Container zwischenzeitlich extern gestoppt/entfernt? Einmal neu sicherstellen + Retry;
                // scheitert auch der, greift der Fail-Open-Fallback im Aufrufer.
                await manager.EnsureRunningAsync(accountId, timeoutCts.Token);
                result = await ExecOnceAsync(name, spec, timeoutCts.Token);
            }

            manager.Touch(accountId);
            return new ProcessResult(result.ExitCode, result.StdOut, result.StdErr);
        }
        catch (OperationCanceledException)
        {
            // Kill-Pfad wie SystemProcessRunner: der Container-Stop beendet den Exec mit; die warme
            // Session liegt im Volume und überlebt, EnsureRunning startet beim nächsten Lauf neu.
            try { await docker.StopAsync(name, CancellationToken.None); }
            catch (DockerUnavailableException) { /* best-effort */ }
            if (ct.IsCancellationRequested)
                throw;
            throw new TimeoutException(
                $"'{spec.FileName}' überschritt das Timeout von {spec.Timeout.TotalSeconds:0}s.");
        }
    }

    private async Task<DockerExecResult> ExecOnceAsync(string name, ProcessSpec spec, CancellationToken ct)
    {
        if (spec.StdIn is not null)
            await docker.WriteFileAsync(name, StdInDirectory, StdInFileName, spec.StdIn, ct);

        // Env-Filter: NUR der Session-Token wandert in den Container. CLAUDE_CONFIG_DIR wird bewusst
        // verworfen — das Container-HOME (= persistentes Volume) gewinnt, damit die Session warm bleibt.
        Dictionary<string, string?>? env = null;
        if (spec.Environment is not null
            && spec.Environment.TryGetValue("CLAUDE_CODE_OAUTH_TOKEN", out var token))
            env = new Dictionary<string, string?> { ["CLAUDE_CODE_OAUTH_TOKEN"] = token };

        // Neutrales CWD im Container (kein ambient CLAUDE.md) — Host-WorkingDirectory ist dort bedeutungslos.
        return await docker.ExecAsync(name, BuildArgv(spec), env, workingDirectory: "/tmp", ct);
    }

    // Ohne stdin: argv direkt. Mit stdin: über die Shell umleiten — "$0"/"$@" trägt die
    // Original-Argv unverändert durch (kein Quoting-Problem), exec ersetzt die Shell durch claude.
    private static IReadOnlyList<string> BuildArgv(ProcessSpec spec)
    {
        if (spec.StdIn is null)
            return [spec.FileName, .. spec.Arguments];
        return ["/bin/sh", "-c", $"exec \"$0\" \"$@\" < {StdInDirectory}/{StdInFileName}",
            spec.FileName, .. spec.Arguments];
    }
}

/// <summary>Factory für SessionSandbox=Docker: pro Account ein an dessen Container gebundener Runner.</summary>
public sealed class DockerSessionRunnerFactory(
    SessionContainerManager manager,
    IDockerClient docker,
    IProcessRunner inProcess,
    SessionSandboxState state,
    ILoggerFactory loggerFactory) : ISessionRunnerFactory
{
    public IProcessRunner ForAccount(int accountId)
        => new DockerSessionRunner(accountId, manager, docker, inProcess, state,
            loggerFactory.CreateLogger<DockerSessionRunner>());
}
```

- [ ] **Step 4: `SessionSelectionFactory` umstellen**

In `src/Naudit.Infrastructure/Ai/ClaudeCode/SessionSelectionFactory.cs`: Using `Naudit.Infrastructure.Ai.Sandbox` ergänzen, `using Naudit.Infrastructure.Process;` entfernen (falls dann ungenutzt), Ctor-Param `IProcessRunner runner` durch `ISessionRunnerFactory runnerFactory` ersetzen und in `ForAccount` die Client-Erzeugung ändern zu:

```csharp
        var sessionClient = new ClaudeCodeChatClient(new AiOptions
        {
            Provider = AiProvider.ClaudeCode,
            Model = options.Model,
            ApiKey = token,
            TimeoutSeconds = aiOptions.TimeoutSeconds,
        }, runnerFactory.ForAccount(accountId));
```

Dann die beiden Test-Konstruktionsstellen anpassen (`tests/Naudit.Tests/AuthorSessionRouterTests.cs` ~Zeile 42, `tests/Naudit.Tests/RoundRobinSessionRouterTests.cs` ~Zeile 35): den bisherigen Runner-Parameter in `new InProcessSessionRunnerFactory(<bisheriger runner>)` wickeln, Using `Naudit.Infrastructure.Ai.Sandbox` ergänzen.

- [ ] **Step 5: DI-Verdrahtung**

In `src/Naudit.Infrastructure/DependencyInjection.cs` direkt VOR `services.AddSingleton<SessionSelectionFactory>();` (Zeile ~92) einfügen (Usings `Naudit.Infrastructure.Ai.Sandbox` und `Naudit.Infrastructure.Docker` ergänzen):

```csharp
        // Session-Sandbox (containerisierte Author/RoundRobin-Sessions): Default None = heutiger
        // In-Process-Runner. Docker ⇒ account-gebundene Runner über den Host-Docker-Socket; jeder
        // Fehlerpfad fällt auf den In-Process-Runner zurück (ein Review scheitert nie an der Sandbox).
        var sandboxOptions = configuration.GetSection("Naudit:Ai:Sandbox").Get<SessionSandboxOptions>()
            ?? new SessionSandboxOptions();
        services.AddSingleton(sandboxOptions);
        services.AddSingleton<SessionSandboxState>();
        if (aiOptions.SessionSandbox == SessionSandbox.Docker)
        {
            services.AddSingleton<IDockerClient>(_ => new SocketDockerClient(sandboxOptions.DockerSocketPath));
            services.AddSingleton(sp => new SessionContainerManager(
                sp.GetRequiredService<IDockerClient>(), sandboxOptions,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<SessionContainerManager>()));
            services.AddSingleton<ISessionRunnerFactory, DockerSessionRunnerFactory>();
        }
        else
        {
            services.AddSingleton<ISessionRunnerFactory>(sp =>
                new InProcessSessionRunnerFactory(sp.GetRequiredService<IProcessRunner>()));
        }
```

- [ ] **Step 6: Wiring-Test schreiben**

`tests/Naudit.Tests/SandboxWiringTests.cs` (Muster `AiClientRouterWiringTests` — gleiche `BaseSettings`/ServiceCollection-Konstruktion):

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Ai.Sandbox;
using Xunit;

namespace Naudit.Tests;

public class SandboxWiringTests
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
    public void Default_usesInProcessRunnerFactory()
    {
        using var sp = Build(BaseSettings());
        Assert.IsType<InProcessSessionRunnerFactory>(sp.GetRequiredService<ISessionRunnerFactory>());
    }

    [Fact]
    public void DockerMode_usesDockerRunnerFactory_andRegistersManager()
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:SessionSandbox"] = "Docker";
        using var sp = Build(settings);

        Assert.IsType<DockerSessionRunnerFactory>(sp.GetRequiredService<ISessionRunnerFactory>());
        Assert.NotNull(sp.GetRequiredService<SessionContainerManager>());
        Assert.NotNull(sp.GetRequiredService<SessionSandboxState>());
    }
}
```

- [ ] **Step 7: Volle Suite grün verifizieren**

Run: `dotnet test Naudit.slnx 2>&1 | tail -3`
Expected: `Passed!` — inkl. der angepassten `AuthorSessionRouterTests`/`RoundRobinSessionRouterTests`.

- [ ] **Step 8: Commit**

```bash
git add src/Naudit.Infrastructure/Ai/Sandbox/ src/Naudit.Infrastructure/Ai/ClaudeCode/SessionSelectionFactory.cs src/Naudit.Infrastructure/DependencyInjection.cs tests/Naudit.Tests/DockerSessionRunnerTests.cs tests/Naudit.Tests/SandboxWiringTests.cs tests/Naudit.Tests/AuthorSessionRouterTests.cs tests/Naudit.Tests/RoundRobinSessionRouterTests.cs
git commit -m "feat(sandbox): DockerSessionRunner + ISessionRunnerFactory-Naht (fail-open, Env-Filter, Retry)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: Idle-Sweeper-Hosted-Service (Start-Ping + Adoption, periodischer Sweep)

**Files:**
- Create: `src/Naudit.Infrastructure/Ai/Sandbox/SandboxSweeperService.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (im Docker-Zweig aus Task 4: `services.AddHostedService<SandboxSweeperService>();` als letzte Zeile ergänzen)
- Modify (nur falls nötig): `src/Naudit.Infrastructure/Naudit.Infrastructure.csproj` — wenn `BackgroundService`/`AddHostedService` nicht auflösen: `<PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.9" />`
- Test: `tests/Naudit.Tests/SandboxSweeperServiceTests.cs`
- Modify: `tests/Naudit.Tests/SandboxWiringTests.cs` (Hosted-Service-Registrierung prüfen)

**Interfaces:**
- Consumes: `IDockerClient.PingAsync`, `SessionContainerManager.AdoptExistingAsync`/`SweepIdleAsync`, `SessionSandboxState.ReportPing`.
- Produces: `SandboxSweeperService : BackgroundService` mit public testbaren Methoden `Task AdoptAsync(CancellationToken)` (Erst-Ping + Adoption) und `Task TickAsync(CancellationToken)` (Re-Ping + Sweep) sowie `public static readonly TimeSpan Interval` (5 min).

- [ ] **Step 1: Failing Tests schreiben**

`tests/Naudit.Tests/SandboxSweeperServiceTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class SandboxSweeperServiceTests
{
    private static (SandboxSweeperService Sweeper, FakeDockerClient Docker, SessionContainerManager Manager, SessionSandboxState State, FakeTime Time)
        Create(FakeDockerClient? docker = null, SessionSandboxOptions? options = null)
    {
        docker ??= new FakeDockerClient();
        var time = new FakeTime();
        var manager = new SessionContainerManager(docker, options ?? new SessionSandboxOptions(),
            NullLogger<SessionContainerManager>.Instance, time);
        var state = new SessionSandboxState();
        var sweeper = new SandboxSweeperService(docker, manager, state,
            NullLogger<SandboxSweeperService>.Instance);
        return (sweeper, docker, manager, state, time);
    }

    [Fact]
    public async Task Adopt_pingFails_setsStateFalse_andSkipsAdoption()
    {
        var docker = new FakeDockerClient { PingResult = false, Containers = { ["naudit-session-1"] = true } };
        var (sweeper, _, manager, state, _) = Create(docker);

        await sweeper.AdoptAsync(CancellationToken.None);

        Assert.False(state.SocketReachable);
        Assert.Null(manager.LastUsed(1)); // keine Adoption ohne erreichbaren Socket
    }

    [Fact]
    public async Task Adopt_pingOk_adoptsExistingContainers()
    {
        var docker = new FakeDockerClient { Containers = { ["naudit-session-8"] = true } };
        var (sweeper, _, manager, state, _) = Create(docker);

        await sweeper.AdoptAsync(CancellationToken.None);

        Assert.True(state.SocketReachable);
        Assert.NotNull(manager.LastUsed(8));
    }

    [Fact]
    public async Task Tick_sweepsIdleContainers()
    {
        var (sweeper, docker, manager, _, time) = Create(
            options: new SessionSandboxOptions { IdleTimeout = TimeSpan.FromHours(1) });
        await manager.EnsureRunningAsync(1);
        time.UtcNow = time.UtcNow.AddHours(2);

        await sweeper.TickAsync(CancellationToken.None);

        Assert.Contains("stop:naudit-session-1", docker.Calls);
    }

    [Fact]
    public async Task Tick_pingRecovers_updatesState_beforeSweeping()
    {
        var (sweeper, docker, _, state, _) = Create();
        state.ReportPing(false);
        docker.PingResult = true;

        await sweeper.TickAsync(CancellationToken.None);

        Assert.True(state.SocketReachable); // Selbstheilung: Runner nutzt Docker wieder
    }

    [Fact]
    public async Task Tick_pingDown_setsStateFalse_andSkipsSweep()
    {
        var docker = new FakeDockerClient { PingResult = false, Containers = { ["naudit-session-1"] = true } };
        var (sweeper, _, _, state, _) = Create(docker);

        await sweeper.TickAsync(CancellationToken.None);

        Assert.False(state.SocketReachable);
        Assert.DoesNotContain(docker.Calls, c => c.StartsWith("stop:"));
    }
}
```

In `tests/Naudit.Tests/SandboxWiringTests.cs` ergänzen:

```csharp
    [Fact]
    public void DockerMode_registersSweeperHostedService()
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:SessionSandbox"] = "Docker";
        using var sp = Build(settings);
        Assert.Contains(sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            s => s is SandboxSweeperService);
    }

    [Fact]
    public void Default_registersNoSweeper()
    {
        using var sp = Build(BaseSettings());
        Assert.DoesNotContain(sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>(),
            s => s is SandboxSweeperService);
    }
```

- [ ] **Step 2: Rot verifizieren**

Run: `dotnet test Naudit.slnx --filter SandboxSweeperServiceTests 2>&1 | tail -5`
Expected: Compile-Fehler.

- [ ] **Step 3: Implementieren**

`src/Naudit.Infrastructure/Ai/Sandbox/SandboxSweeperService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Docker;

namespace Naudit.Infrastructure.Ai.Sandbox;

/// <summary>Hintergrunddienst der Session-Sandbox (nur bei SessionSandbox=Docker registriert):
/// beim Start Ping (Fail-Open-Zustand setzen) + Adoption bestehender Container, danach alle
/// 5 Minuten Re-Ping (Selbstheilung nach Socket-Ausfall) + Idle-Sweep. Durchweg fail-quiet —
/// Sandbox-Probleme stören nie den Host.</summary>
public sealed class SandboxSweeperService(
    IDockerClient docker,
    SessionContainerManager manager,
    SessionSandboxState state,
    ILogger<SandboxSweeperService> logger) : BackgroundService
{
    /// <summary>Bewusst kurz gegen das lange IdleTimeout: der Tick ist zugleich die Re-Ping-Sonde.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await AdoptAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await TickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Host-Stopp — regulär beenden.
        }
    }

    /// <summary>Startschritt: Ping + Adoption über das Namens-Präfix (public für direkte Tests).</summary>
    public async Task AdoptAsync(CancellationToken ct)
    {
        if (!await PingAndReportAsync(ct))
            return;
        try
        {
            await manager.AdoptExistingAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Session-Sandbox: Adoption bestehender Container fehlgeschlagen.");
        }
    }

    /// <summary>Ein Sweep-Tick: neu pingen, dann Idle-Container stoppen (public für direkte Tests).</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!await PingAndReportAsync(ct))
            return;
        try
        {
            await manager.SweepIdleAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Session-Sandbox: Idle-Sweep fehlgeschlagen.");
        }
    }

    private async Task<bool> PingAndReportAsync(CancellationToken ct)
    {
        var previous = state.SocketReachable;
        var ok = await docker.PingAsync(ct);
        state.ReportPing(ok);
        if (!ok && previous != false)
            logger.LogWarning("Session-Sandbox: docker.sock nicht erreichbar/nutzbar — " +
                "Abo-Sessions laufen in-process weiter (Fallback). Socket-Mount + group_add prüfen, " +
                "siehe docs/session-sandbox.md.");
        else if (ok && previous == false)
            logger.LogInformation("Session-Sandbox: docker.sock wieder erreichbar — Sandbox aktiv.");
        return ok;
    }
}
```

Im Docker-Zweig von `DependencyInjection.cs` (Task 4) als letzte Zeile ergänzen:

```csharp
            services.AddHostedService<SandboxSweeperService>();
```

Falls `BackgroundService`/`AddHostedService`/`IHostedService` nicht kompilieren: `Microsoft.Extensions.Hosting.Abstractions` Version `10.0.9` als PackageReference in `Naudit.Infrastructure.csproj` ergänzen (bei den anderen 10.0.9-Referenzen einsortieren).

- [ ] **Step 4: Grün verifizieren (volle Suite — DI-Probe/Recovery-Tests mitbetroffen)**

Run: `dotnet test Naudit.slnx 2>&1 | tail -3`
Expected: `Passed!`

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Ai/Sandbox/SandboxSweeperService.cs src/Naudit.Infrastructure/DependencyInjection.cs src/Naudit.Infrastructure/Naudit.Infrastructure.csproj tests/Naudit.Tests/SandboxSweeperServiceTests.cs tests/Naudit.Tests/SandboxWiringTests.cs
git commit -m "feat(sandbox): Idle-Sweeper-Hosted-Service — Start-Ping/Adoption + periodischer Sweep (selbstheilend)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

(`Naudit.Infrastructure.csproj` nur adden, wenn tatsächlich geändert.)

---

### Task 6: Lifecycle-Hooks — Pool-Austritt/Token-Löschung entfernen Container + Volume

**Files:**
- Modify: `src/Naudit.Infrastructure/Ui/ClaudeSessionService.cs`
- Test: erweitert `tests/Naudit.Tests/ClaudeSessionServiceTests.cs`

**Interfaces:**
- Consumes: `SessionContainerManager.RemoveAsync(accountId)` (Task 3).
- Produces: `ClaudeSessionService`-Ctor bekommt zwei **optionale** Parameter (`SessionContainerManager? sandbox = null, ILogger<ClaudeSessionService>? logger = null`) — MS-DI löst fehlende Registrierungen über die Default-Werte auf (im None-Modus ist kein Manager registriert ⇒ null); bestehende Test-Konstruktionen kompilieren unverändert.

- [ ] **Step 1: Failing Tests schreiben**

Bestehende Datei `tests/Naudit.Tests/ClaudeSessionServiceTests.cs` lesen und ihrem Konstruktions-Muster folgend (DbContext/DataProtection wie dort) diese Fälle ergänzen — `FakeDockerClient` + echter `SessionContainerManager` als Sandbox-Kollaborateur:

```csharp
    private static SessionContainerManager Sandbox(FakeDockerClient docker)
        => new(docker, new SessionSandboxOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionContainerManager>.Instance, new FakeTime());

    [Fact]
    public async Task RemoveToken_removesSandboxContainerAndVolume()
    {
        // Arrange wie die bestehenden Tests dieser Datei (Db + DataProtection + Account mit Token),
        // zusätzlich: FakeDockerClient mit laufendem Container des Accounts.
        var docker = new FakeDockerClient();
        docker.Containers[$"naudit-session-{acct.Id}"] = true;
        docker.Volumes.Add($"naudit-session-{acct.Id}");
        var service = new ClaudeSessionService(db, dataProtection, Sandbox(docker));

        await service.RemoveTokenAsync(acct.Id);

        Assert.Empty(docker.Containers); // stop + rm + rmvol gelaufen
        Assert.Empty(docker.Volumes);
    }

    [Fact]
    public async Task SetShareInPool_false_removesContainer_true_doesNot()
    {
        var docker = new FakeDockerClient();
        docker.Containers[$"naudit-session-{acct.Id}"] = true;
        var service = new ClaudeSessionService(db, dataProtection, Sandbox(docker));

        await service.SetShareInPoolAsync(acct.Id, share: true);
        Assert.NotEmpty(docker.Containers); // Opt-in räumt nichts ab

        await service.SetShareInPoolAsync(acct.Id, share: false);
        Assert.Empty(docker.Containers);
    }

    [Fact]
    public async Task RemoveToken_sandboxFailure_doesNotFailTokenRemoval()
    {
        // ThrowingDockerClient: IDockerClient, dessen Methoden alle DockerUnavailableException werfen
        // (kleine private Testklasse in dieser Datei; nur die vom RemoveAsync-Pfad berührten Methoden
        // müssen werfen, Rest Task.CompletedTask/Defaults).
        var service = new ClaudeSessionService(db, dataProtection, Sandbox(throwingDocker));

        await service.RemoveTokenAsync(acct.Id); // darf NICHT werfen

        // Token ist trotzdem weg (wie im bestehenden RemoveToken-Test dieser Datei prüfen).
    }

    [Fact]
    public async Task RemoveToken_withoutSandbox_isUnchanged()
    {
        var service = new ClaudeSessionService(db, dataProtection); // sandbox = null (None-Modus)
        await service.RemoveTokenAsync(acct.Id); // wie bisher, kein Docker-Kontakt
    }
```

(Platzhalter `acct`/`db`/`dataProtection` durch das konkrete Arrange-Muster der bestehenden Datei ersetzen — die Tests dort zeigen, wie Account + Service konstruiert werden.)

- [ ] **Step 2: Rot verifizieren**

Run: `dotnet test Naudit.slnx --filter ClaudeSessionServiceTests 2>&1 | tail -5`
Expected: Compile-Fehler (Ctor-Überladung fehlt) bzw. Fail.

- [ ] **Step 3: Implementieren**

`src/Naudit.Infrastructure/Ui/ClaudeSessionService.cs`: Usings `Microsoft.Extensions.Logging` + `Naudit.Infrastructure.Ai.Sandbox` ergänzen; Primary-Ctor erweitern:

```csharp
public sealed class ClaudeSessionService(
    NauditDbContext db,
    IDataProtectionProvider dataProtection,
    SessionContainerManager? sandbox = null,
    ILogger<ClaudeSessionService>? logger = null)
```

Am Ende von `RemoveTokenAsync` (nach dem bestehenden DB-Save) und in `SetShareInPoolAsync` (nur im `share == false`-Fall, nach dem Save) jeweils:

```csharp
        await RemoveSandboxAsync(accountId, ct);
```

Neue private Methode:

```csharp
    // Sandbox-Lifecycle: ohne Token/Pool-Opt-in soll der Account-Container samt Volume (enthält
    // CLI-Credentials!) verschwinden. Best-effort — ein Docker-Fehler kippt nie die DB-Operation.
    // Im Author-Modus kostet ein Pool-Austritt so schlimmstenfalls einen Kaltstart beim nächsten Review.
    private async Task RemoveSandboxAsync(int accountId, CancellationToken ct)
    {
        if (sandbox is null)
            return;
        try
        {
            await sandbox.RemoveAsync(accountId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex,
                "Session-Sandbox: Container-Abbau für Konto {AccountId} fehlgeschlagen (best-effort).", accountId);
        }
    }
```

- [ ] **Step 4: Grün verifizieren**

Run: `dotnet test Naudit.slnx --filter ClaudeSessionServiceTests 2>&1 | tail -3`
Expected: `Passed!` (bestehende + neue Tests).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Ui/ClaudeSessionService.cs tests/Naudit.Tests/ClaudeSessionServiceTests.cs
git commit -m "feat(sandbox): Pool-Austritt/Token-Löschung entfernt Account-Container + Credential-Volume (best-effort)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: Status-Endpoint `GET /api/me/session-sandbox` + SPA-Statuszeile

**Files:**
- Create: `src/Naudit.Web/Endpoints/SessionSandboxEndpoints.cs`
- Modify: `src/Naudit.Web/Program.cs` (im Gesund-Block direkt nach dem `app.MapGitHubAppEndpoints(...)`-Aufruf, Zeile ~408; Using `Naudit.Infrastructure.Ai` ist dort ggf. schon vorhanden — sonst ergänzen, plus `Naudit.Infrastructure.Ai.Sandbox` im Endpoint-File)
- Modify: `src/frontend/src/api/types.ts`, `src/frontend/src/hooks/queries.ts`, `src/frontend/src/components/ClaudeSessionCard.tsx`
- Test: `tests/Naudit.Tests/SessionSandboxEndpointTests.cs`

**Interfaces:**
- Consumes: `SessionSandboxState.SocketReachable` (Task 4), `SessionContainerManager.CountRunningAsync` (Task 3), `AiOptions.SessionSandbox` (Task 1), `CurrentAccount.GetAsync` (bestehend).
- Produces: `GET /api/me/session-sandbox` → `{ "mode": "Docker", "socketReachable": true|false|null, "liveContainers": number|null }`; nur gemappt bei `SessionSandbox=Docker` (sonst 404 — Muster `GitHubAppEndpoints`); `401` ohne Session.

- [ ] **Step 1: Failing Endpoint-Tests schreiben**

`tests/Naudit.Tests/SessionSandboxEndpointTests.cs` — Konstruktion und Login-Helfer 1:1 dem Muster von `GitHubAppEndpointTests` folgen (Factory via `WithWebHostBuilder` + `UseSetting`-Baseline von dort, Login über den dort verwendeten Auth-Flow mit `root`/`passwort123`; die Datei zeigt das komplette Muster — nachlesen und spiegeln):

```csharp
    [Fact]
    public async Task WithoutDockerMode_routeIsNotMapped()
    {
        using var app = App(sandboxDocker: false);
        using var client = /* eingeloggter Client wie im GitHubApp-Muster */;
        var resp = await client.GetAsync("/api/me/session-sandbox");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task DockerMode_unauthenticated_is401()
    {
        using var app = App(sandboxDocker: true);
        using var client = app.CreateClient(); // ohne Login
        var resp = await client.GetAsync("/api/me/session-sandbox");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task DockerMode_returnsModeAndStatusFields()
    {
        using var app = App(sandboxDocker: true);
        using var client = /* eingeloggter Client */;

        var resp = await client.GetAsync("/api/me/session-sandbox");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("Docker", json.RootElement.GetProperty("mode").GetString());
        Assert.True(json.RootElement.TryGetProperty("socketReachable", out _));
        // liveContainers ist null, wenn kein Docker-Socket erreichbar ist (CI) — Feld muss existieren.
        Assert.True(json.RootElement.TryGetProperty("liveContainers", out _));
    }
```

`App(sandboxDocker: true)` setzt zusätzlich `b.UseSetting("Naudit:Ai:SessionSandbox", "Docker")` und `b.UseSetting("Naudit:Ai:Sandbox:DockerSocketPath", "/nonexistent/naudit-test.sock")` (deterministisch: nie erreichbar, Ping ⇒ false, `CountRunningAsync` wirft ⇒ `liveContainers: null` — kein echtes Docker im Test).

- [ ] **Step 2: Rot verifizieren**

Run: `dotnet test Naudit.slnx --filter SessionSandboxEndpointTests 2>&1 | tail -5`
Expected: FAIL (Route fehlt ⇒ 404 statt 200/401 in den Docker-Fällen).

- [ ] **Step 3: Endpoint implementieren**

`src/Naudit.Web/Endpoints/SessionSandboxEndpoints.cs`:

```csharp
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Infrastructure.Data;

namespace Naudit.Web.Endpoints;

/// <summary>Sandbox-Status fürs SPA (Statuszeile auf der Profilseite). Nur gemappt, wenn
/// SessionSandbox=Docker — sonst existiert die Route nicht (404) und das SPA zeigt nichts
/// (Muster GitHubAppEndpoints).</summary>
public static class SessionSandboxEndpoints
{
    public static void MapSessionSandboxEndpoints(this WebApplication app, AiOptions ai)
    {
        if (ai.SessionSandbox != SessionSandbox.Docker)
            return;

        app.MapGet("/api/me/session-sandbox",
            async (HttpContext ctx, NauditDbContext db, SessionSandboxState state, SessionContainerManager manager) =>
            {
                var acct = await CurrentAccount.GetAsync(ctx, db);
                if (acct is null) return Results.Unauthorized();

                int? live = null;
                try
                {
                    live = await manager.CountRunningAsync(ctx.RequestAborted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Socket weg ⇒ null; socketReachable erzählt dem SPA den Rest.
                }
                return Results.Ok(new
                {
                    mode = "Docker",
                    socketReachable = state.SocketReachable,
                    liveContainers = live,
                });
            }).RequireAuthorization();
    }
}
```

In `src/Naudit.Web/Program.cs` direkt nach dem `app.MapGitHubAppEndpoints(...)`-Aufruf (Ende Zeile ~407, noch im selben Gesund-Block):

```csharp
        // Session-Sandbox-Status fürs SPA — mappt sich selbst nur bei SessionSandbox=Docker.
        app.MapSessionSandboxEndpoints(app.Services.GetRequiredService<AiOptions>());
```

- [ ] **Step 4: Backend grün verifizieren**

Run: `dotnet test Naudit.slnx 2>&1 | tail -3`
Expected: `Passed!`

- [ ] **Step 5: SPA-Statuszeile**

`src/frontend/src/api/types.ts` — ergänzen:

```ts
export interface SessionSandboxDto {
  mode: string;
  socketReachable: boolean | null;
  liveContainers: number | null;
}
```

`src/frontend/src/hooks/queries.ts` — neben `useGithubApp` (gleiches Fehler-Muster wie dort — die Route existiert im None-Modus nicht):

```ts
export function useSessionSandbox() {
  return useQuery({
    queryKey: ["session-sandbox"],
    queryFn: () => api<SessionSandboxDto>("/api/me/session-sandbox"),
    retry: false,
  });
}
```

(`SessionSandboxDto` in den Type-Import oben aufnehmen. Prüfen, wie `useGithubApp` 404 behandelt — exakt dieselbe Option übernehmen, z. B. `retry: false` + Fehler ⇒ Banner/Zeile entfällt.)

`src/frontend/src/components/ClaudeSessionCard.tsx` — `useSessionSandbox()` aufrufen und unterhalb der bestehenden Status-Pills eine Zeile im Stil der Karte rendern (Klassen von den Nachbar-Elementen der Karte übernehmen; Fehler/`isError` ⇒ nichts rendern):

```tsx
{sandbox.data && (
  <p className="text-xs text-neutral-500">
    Session sandbox:{" "}
    {sandbox.data.socketReachable === false
      ? "fallback in-process (docker.sock unreachable)"
      : `active${sandbox.data.liveContainers != null ? ` · ${sandbox.data.liveContainers} container(s)` : ""}`}
  </p>
)}
```

- [ ] **Step 6: Frontend verifizieren**

Run: `cd src/frontend && npm run lint && npm run build; cd ../..`
Expected: Lint 0 Fehler, `tsc --noEmit` + Vite-Build erfolgreich.

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Web/Endpoints/SessionSandboxEndpoints.cs src/Naudit.Web/Program.cs tests/Naudit.Tests/SessionSandboxEndpointTests.cs src/frontend/src/api/types.ts src/frontend/src/hooks/queries.ts src/frontend/src/components/ClaudeSessionCard.tsx
git commit -m "feat(sandbox): GET /api/me/session-sandbox + Statuszeile in der ClaudeSessionCard

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: Dokumentation + Gesamtabnahme

**Files:**
- Create: `docs/session-sandbox.md`
- Modify: `docs/deployment.md` (Abschnitt Socket-Mount/`group_add`)
- Modify: `docs/author-sessions.md` (Querverweis + kurzer Absatz "Runtime isolation")
- Modify: `CLAUDE.md` (neuer Extension-Point-Bullet)

**Interfaces:** —

- [ ] **Step 1: `docs/session-sandbox.md` schreiben (englisch)**

Inhalt (vollständig ausformulieren, Stil wie `docs/author-sessions.md`/`docs/review-memory.md`):

1. **Purpose** — warm per-account sessions + isolation for Author/RoundRobin subscription runs; sibling containers via the host Docker socket; strictly additive (`None` default = today's in-process runner). Explizit: this feature is about isolation/performance, **not** about concealing round-robin pooling from Anthropic; the ToS caveat from `docs/author-sessions.md` is unchanged.
2. **Config keys** (Tabelle): `Naudit:Ai:SessionSandbox` (`None`|`Docker`), `Naudit:Ai:Sandbox:IdleTimeout` (default `2.00:00:00`), `MaxLiveContainers` (default 5), `DockerSocketPath` (default `/var/run/docker.sock`), `Image` (default empty ⇒ self-inspection). Alle DB-verwaltbar über die Settings-Seite (Restart erforderlich).
3. **Lifecycle** — container `naudit-session-<accountId>` (same Naudit image, `sleep infinity`), named volume mounted at `/home/app` (CLI credentials ⇒ warm auth survives stop **and** restart), exec per review (`sh -c 'exec "$0" "$@" < /tmp/naudit-stdin'`, token as exec env), per-account lock, LRU stop at `MaxLiveContainers`, idle sweeper (stop, never rm), adoption after Naudit restart, container+volume removal on token deletion/pool opt-out.
4. **Fail-open matrix** — socket missing/ping fails ⇒ in-process fallback (self-healing re-ping every 5 min); container start fails ⇒ one retry then in-process; exec timeout ⇒ container stop + `TimeoutException` (same semantics as in-process); claude non-zero exit ⇒ normal error path (not a sandbox failure).
5. **Operations** — `MaxLiveContainers` is the real resource bound, the sweeper is the safety net; volumes persist deliberately; how to reset one account (`docker rm -f naudit-session-<id> && docker volume rm naudit-session-<id>` or token re-save).
6. **Security note (prominent)** — mounting `docker.sock` ≈ root on the host: anyone who can reach the socket owns the machine; the subscription token is visible in exec env (`docker inspect` during a run) and its credentials live in the named volume; the volume intentionally survives restarts. Only run on hosts where Naudit itself is trusted with root-equivalent access.
7. **Deployment snippet** (Compose/Coolify):

```yaml
services:
  naudit:
    image: ghcr.io/benediktnau/naudit:latest
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
    group_add:
      - "984"   # GID of the docker group on the HOST: stat -c '%g' /var/run/docker.sock
    environment:
      Naudit__Ai__SessionSandbox: "Docker"
```

- [ ] **Step 2: `docs/deployment.md` + `docs/author-sessions.md` ergänzen**

`deployment.md`: kurzer Abschnitt "Session sandbox (optional)" mit dem Snippet + GID-Hinweis + Link auf `docs/session-sandbox.md`. `author-sessions.md`: ein Absatz unter dem Routing-Teil ("Runtime isolation: with `Naudit:Ai:SessionSandbox=Docker` each account's CLI runs in its own long-lived sibling container, see `docs/session-sandbox.md`").

- [ ] **Step 3: `CLAUDE.md`-Bullet ergänzen**

In der Extension-Points-Liste nach dem Author-Sessions-Bullet:

```markdown
- **Session sandbox (containerised subscription sessions):** `Naudit:Ai:SessionSandbox = None | Docker`
  (default `None` = in-process CLI runs, today's behaviour). `Docker` moves Author/RoundRobin session
  runs into long-lived sibling containers per account (host Docker socket, same Naudit image,
  `sleep infinity` + `docker exec`; named volume `naudit-session-<accountId>` at `/home/app` keeps
  CLI auth warm). Seam: `ISessionRunnerFactory` (`src/Naudit.Infrastructure/Ai/Sandbox/`) consumed by
  `SessionSelectionFactory.ForAccount`; `SessionContainerManager` owns lifecycle (per-account locks,
  LRU cap `MaxLiveContainers`, idle sweeper stops after `IdleTimeout`, adoption after restart);
  `IDockerClient`/`SocketDockerClient` (`src/Naudit.Infrastructure/Docker/`) is a hand-rolled
  Engine-API client over the Unix socket (no new NuGet). Fail-open everywhere: any Docker error falls
  back to the in-process runner — a review never fails because of the sandbox. Opt-in integration
  test via `NAUDIT_TEST_DOCKER=1`. Status: `GET /api/me/session-sandbox` (mapped only in Docker
  mode). See `docs/session-sandbox.md`.
```

- [ ] **Step 4: Gesamtabnahme**

```bash
dotnet test Naudit.slnx 2>&1 | tail -3
cd src/frontend && npm run lint && npm run build; cd ../..
```

Expected: volle Suite `Passed!` (Baseline war 534 + neue Tests), Lint/Build sauber. Falls lokal Docker verfügbar ist, zusätzlich einmal `NAUDIT_TEST_DOCKER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SocketDockerClientTests`.

- [ ] **Step 5: Commit**

```bash
git add docs/session-sandbox.md docs/deployment.md docs/author-sessions.md CLAUDE.md
git commit -m "docs(sandbox): session-sandbox documented (config, lifecycle, fail-open, security, deployment)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Self-Review (durchgeführt)

- **Spec-Abdeckung:** Config-Keys (T1), `IDockerClient`+Socket-Client (T2), Manager mit run/start/stop/rm, Locks, LRU-Cap, Adoption (T3), account-gebundener Runner + Env-Filter + Fail-Open + `SessionSelectionFactory`-Injektion (T4), Idle-Sweeper (T5), Pool-Austritt/Account-Abbau (T6), WebUI-Status (T7), Doku + Security-Notiz + `group_add` (T8). Nicht übernommen aus der Spec: Laufzeit-`setgroups` (technisch ohne CAP_SETGID unmöglich — ersetzt durch Ping-Probe + Log + Doku), Setup-Wizard-Live-Test (Spec: "optional" — YAGNI), MCP im Container (Spec: explizit verschoben).
- **Typ-Konsistenz:** `ISessionRunnerFactory.ForAccount(int) -> IProcessRunner`; `SessionContainerManager.ContainerName/EnsureRunningAsync/AcquireLockAsync/Touch/LastUsed/SweepIdleAsync/AdoptExistingAsync/RemoveAsync/CountRunningAsync` werden in T4/T5/T6/T7 exakt mit diesen Namen konsumiert; `DockerUnavailableException` ist der einzige Fail-Open-Typ; `SessionSandboxState.ReportPing/SocketReachable` in T4/T5/T7.
- **Platzhalter:** T6/T7 verweisen bewusst auf das Arrange-/Login-Muster bestehender Testdateien (`ClaudeSessionServiceTests`, `GitHubAppEndpointTests`) statt es zu duplizieren — die Zieldateien liegen dem Implementer vor; alle neuen Produktivcode-Blöcke sind vollständig.
