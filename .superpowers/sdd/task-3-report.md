# Task 3 Report: Bidirectional attached `docker exec` on the Docker seam

## Status: DONE

## What was implemented

- **`src/Naudit.Infrastructure/Docker/DockerExecStream.cs`** (new) — `sealed class DockerExecStream
  : IAsyncDisposable` wrapping `Stdin`/`Stdout` streams plus an `IAsyncDisposable underlying`;
  `DisposeAsync` closes the underlying socket connection.
- **`src/Naudit.Infrastructure/Docker/DockerStreamDemux.cs`** — added `ReadFrameAsync(Stream, CancellationToken)
  -> (byte StreamType, byte[] Payload)?`, an incremental single-frame reader (8-byte header: [0]=stream
  type, [4..7]=big-endian length) returning `null` at EOF. Existing `ReadAsync` (buffer-to-completion) is
  untouched.
- **`src/Naudit.Infrastructure/Docker/IDockerClient.cs`** — added
  `Task<DockerExecStream> ExecStreamAsync(name, argv, environment, workingDirectory, ct = default)`.
- **`src/Naudit.Infrastructure/Docker/SocketDockerClient.cs`**:
  - Extracted the inline `ConnectCallback` lambda into `private static ValueTask<Stream>
    ConnectRawAsync(string path, CancellationToken ct)` — opens `Socket(AddressFamily.Unix, Stream,
    Unspecified)`, `ConnectAsync(new UnixDomainSocketEndPoint(path), ct)`, returns `new
    NetworkStream(socket, ownsSocket: true)`; disposes the socket on connect failure. The
    `SocketsHttpHandler.ConnectCallback` field now just calls `ConnectRawAsync(socketPath, ct)` — same
    connect logic, single source of truth.
  - Added `ExecStreamAsync`: (1) `exec create` over the normal buffered `_http`/`SendAsync` path to get
    the exec id; (2) a hand-written raw HTTP/1.1 request (`POST /exec/{id}/start ... Upgrade: tcp`) written
    directly to a fresh `ConnectRawAsync` socket, because `HttpClient` cannot hand back the write side of
    a connection; (3) `ConsumeHttpHeadersAsync` scans byte-by-byte for the first `\r\n\r\n` (status-line
    agnostic — works for both `101 UPGRADED` and `200 OK`, confirmed against the real engine below); (4)
    wraps the remaining duplex socket in a `DemuxReadStream` (incrementally consumes `DockerStreamDemux
    .ReadFrameAsync`, keeps stdout frames, discards stderr) and returns `DockerExecStream(stdin: raw,
    stdout: demuxed, underlying: RawStreamDisposable(raw))`.
  - Connect-phase transport errors (`SocketException`/`IOException`) are wrapped as
    `DockerUnavailableException` before any raw stream exists; failures after the raw socket is open
    (start-request write/read, header-scan EOF, or any other exception) `await raw.DisposeAsync()` before
    rethrowing/wrapping — no leaked sockets on any failure path.
- **`tests/Naudit.Tests/Fakes/FakeDockerClient.cs`** — `ExecStreamAsync` records `(Container, Argv)` in
  `ExecStreamCalls`, returns a `DockerExecStream` backed by a `CapturingStream` (captures written bytes
  into `LastExecStdin`) for `Stdin` and a `MemoryStream(NextExecStdout ?? [])` for `Stdout`.
- **`tests/Naudit.Tests/AccountServiceTests.cs`** / **`tests/Naudit.Tests/ClaudeSessionServiceTests.cs`** —
  mechanical `ExecStreamAsync => throw new NotSupportedException()` added to each local
  `ThrowingDockerClient` to keep the build green (neither test double exercises the exec-stream path).
- **`tests/Naudit.Tests/SocketDockerClientTests.cs`** — new gated `ExecStream_roundtripsStdinToStdout`
  (real Docker; `cat` in a `sleep infinity` container, write via `Stdin`, read back via `Stdout`).
- **`tests/Naudit.Tests/FakeDockerExecStreamTests.cs`** (new) — fake-based
  `ExecStream_fake_echoesScriptedStdout_andRecordsArgv`, always runs (no Docker needed).

## Connect-logic reuse decision

The brief's implementer note called for extracting `ConnectRawAsync` if the existing connect was only an
inline `ConnectCallback` lambda — that was exactly the case here. Extracted it as a `private static
ValueTask<Stream> ConnectRawAsync(string path, CancellationToken ct)` with identical body to the original
lambda (socket create → `ConnectAsync` → `NetworkStream`, dispose-on-failure), and pointed the
`SocketsHttpHandler.ConnectCallback` at it. Both the pooled `HttpClient` connections and the ad-hoc
duplex connection in `ExecStreamAsync` now share one connect implementation — no behavioural drift
between the two paths, which matters here since the real-Docker test is the only thing that can catch a
subtle divergence.

## TDD: compile-red before green

Step 1–2 (test-first): appended the gated real-Docker test to `SocketDockerClientTests.cs` and created
`FakeDockerExecStreamTests.cs`, both referencing `ExecStreamAsync`/`DockerExecStream`/`NextExecStdout`/
`ExecStreamCalls`/`LastExecStdin`, none of which existed yet.

`dotnet build Naudit.slnx` failed as expected (Step 3):

```
FakeDockerExecStreamTests.cs(13,16): error CS1061: 'FakeDockerClient' does not contain a definition for 'NextExecStdout' ...
FakeDockerExecStreamTests.cs(15,45): error CS1061: 'FakeDockerClient' does not contain a definition for 'ExecStreamAsync' ...
FakeDockerExecStreamTests.cs(23,32): error CS1061: 'FakeDockerClient' does not contain a definition for 'ExecStreamCalls' ...
FakeDockerExecStreamTests.cs(24,81): error CS1061: 'FakeDockerClient' does not contain a definition for 'LastExecStdin' ...
SocketDockerClientTests.cs(110,49): error CS1061: 'SocketDockerClient' does not contain a definition for 'ExecStreamAsync' ...
5 Error(s)
```

After implementing `DockerExecStream`/`DockerStreamDemux.ReadFrameAsync`/`IDockerClient.ExecStreamAsync`/
`SocketDockerClient.ExecStreamAsync` (Steps 4–6), the build failed only on the two `IDockerClient` doubles
missing the new interface member (`FakeDockerClient`, both `ThrowingDockerClient`s) — CS0535. Adding the
Step 7 stubs turned the build green.

## Test results

**Fake unit test** (`dotnet test ... --filter FakeDockerExecStreamTests`):
```
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 31 ms
```

**Full suite** (`DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`):
```
Passed!  - Failed: 0, Passed: 704, Skipped: 0, Total: 704, Duration: 23 s
```
Baseline was 702; +2 as expected (the always-on fake unit test, and the gated real-Docker test which
still runs — and now also compiles into — a normal `dotnet test` invocation without `NAUDIT_TEST_DOCKER`,
counted as passed via early `return`). No `GitWorkspaceProviderTests` flake observed.

**REAL-DOCKER gated test** (`NAUDIT_TEST_DOCKER=1 dotnet test tests/Naudit.Tests/Naudit.Tests.csproj
--filter SocketDockerClientTests`, Docker Engine 29.5.3 at `/var/run/docker.sock`), verbose:
```
[xUnit.net 00:00:00.37]   Discovered:  Naudit.Tests
[xUnit.net 00:00:00.41]   Starting:    Naudit.Tests
  Passed Naudit.Tests.SocketDockerClientTests.Ping_missingSocket_isFalse_notThrow [52 ms]
  Passed Naudit.Tests.SocketDockerClientTests.NetworkLifecycle_create_list_remove [307 ms]
  Passed Naudit.Tests.SocketDockerClientTests.FullLifecycle_run_exec_stop_start_remove [6 s]
[xUnit.net 00:00:07.63]   Finished:    Naudit.Tests
  Passed Naudit.Tests.SocketDockerClientTests.ExecStream_roundtripsStdinToStdout [493 ms]

Test Run Successful.
Total tests: 4
     Passed: 4
 Total time: 8.3336 Seconds
```
`ExecStream_roundtripsStdinToStdout` passed on the **first attempt** — no iteration on the duplex
protocol was needed. Confirmed no leftover `naudit-dast-pw-*`/`naudit-test-*` containers after the run
(`docker ps -a --filter name=naudit-dast-pw-` / `name=naudit-test-` both empty) — the `finally` +
`RemoveContainerAsync` teardown works correctly even with the exec socket still notionally attached
(the exec process is `cat`, which the container removal force-kills).

## Self-review: resource lifetimes

- **Raw socket disposed on every failure path?** Yes. `ConnectRawAsync` disposes its own `Socket` if
  `ConnectAsync` throws (pre-existing pattern, reused). In `ExecStreamAsync`, the connect call itself is
  outside the second `try` (nothing to dispose if it fails) and wraps `SocketException`/`IOException` as
  `DockerUnavailableException`. Once `raw` exists, the following `try/catch` around the write of the
  start-request, `ConsumeHttpHeadersAsync`, and stream construction disposes `raw` in every catch branch
  (both the specific `SocketException`/`IOException` branch and the catch-all) before rethrowing/wrapping.
  On success, ownership of `raw` transfers to `DockerExecStream` via `RawStreamDisposable`, whose
  `DisposeAsync` disposes it — `await using var exec = await docker.ExecStreamAsync(...)` in the test
  closes the socket cleanly at scope exit.
- **`DemuxReadStream` handles partial reads and stderr frames?** Yes. `ReadFrameAsync`'s
  `ReadExactlyOrEofAsync` loops until the full header/payload is read or the stream hits EOF (`n == 0`),
  so a `NetworkStream` returning short reads (routine on a socket) is handled — this is the same pattern
  as the existing buffered `ReadAsync`/`ReadUpToAsync`. `DemuxReadStream.ReadAsync` loops past
  `StreamType == 2` (stderr) frames and zero-length frames, only surfacing stdout payload bytes; it copies
  at most `buffer.Length` bytes per call and buffers the remainder in `_pending`/`_offset` for the next
  read, so a caller with a small buffer against a large payload still gets every byte.
- **`DisposeAsync` closes the socket?** Yes — `DockerExecStream.DisposeAsync` awaits
  `underlying.DisposeAsync()`, `underlying` is `RawStreamDisposable(raw)`, whose `DisposeAsync` awaits
  `raw.DisposeAsync()`; `raw` is the `NetworkStream` from `ConnectRawAsync` (`ownsSocket: true`), so
  disposing it also disposes/closes the underlying `Socket`.

## Files changed

- `src/Naudit.Infrastructure/Docker/DockerExecStream.cs` (new)
- `src/Naudit.Infrastructure/Docker/DockerStreamDemux.cs`
- `src/Naudit.Infrastructure/Docker/IDockerClient.cs`
- `src/Naudit.Infrastructure/Docker/SocketDockerClient.cs`
- `tests/Naudit.Tests/Fakes/FakeDockerClient.cs`
- `tests/Naudit.Tests/AccountServiceTests.cs` (mechanical `ThrowingDockerClient` stub)
- `tests/Naudit.Tests/ClaudeSessionServiceTests.cs` (mechanical `ThrowingDockerClient` stub)
- `tests/Naudit.Tests/SocketDockerClientTests.cs`
- `tests/Naudit.Tests/FakeDockerExecStreamTests.cs` (new)

## Concerns

- None blocking. The brief's implementer note anticipated needing to adjust `ConsumeHttpHeadersAsync` for
  a `101 UPGRADED` vs `200 OK` status-line difference; the header scan as written is already
  status-line-agnostic (scans purely for `\r\n\r\n`) and worked unmodified against the real engine
  (29.5.3) on the first run — no iteration was required.
- `ConsumeHttpHeadersAsync` reads one byte at a time via `ReadAsync` over a `NetworkStream` — fine for a
  small header (a few hundred bytes, one-time per exec-stream), not a hot path; noted for awareness, not
  changed since it mirrors the brief exactly and correctness/lifetime mattered more than micro-throughput
  here.
- The two `ThrowingDockerClient` doubles remain duplicated across `AccountServiceTests.cs` and
  `ClaudeSessionServiceTests.cs` (pre-existing duplication, documented in the source comments as
  intentional); the new stub was added identically to both, consistent with that existing choice.
