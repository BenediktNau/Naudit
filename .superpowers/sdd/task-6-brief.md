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

