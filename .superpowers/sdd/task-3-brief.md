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

