using System.Text.Json;
using Microsoft.Extensions.AI;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Naudit.Infrastructure.Process;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ClaudeCodeChatClientTests
{
    // Baut ein --output-format-json-Envelope wie die CLI es liefert.
    private static string Envelope(string? result, bool isError = false, string subtype = "success")
        => JsonSerializer.Serialize(new { type = "result", subtype, is_error = isError, result });

    // Envelope mit usage-Objekt, wie die CLI es unter --output-format json mitliefert.
    private static string EnvelopeWithUsage(string result, long inputTokens, long outputTokens)
        => JsonSerializer.Serialize(new
        {
            type = "result", subtype = "success", is_error = false, result,
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
        });

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

        var args = stub.LastSpec!.Arguments.ToList();
        Assert.Contains("-p", args);
        Assert.Contains("--output-format", args);
        Assert.Contains("json", args);
        Assert.Contains("--max-turns", args);
        Assert.Equal("1", args[args.IndexOf("--max-turns") + 1]);  // genau ein Turn
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

        var args = stub.LastSpec!.Arguments.ToList();
        Assert.Equal("sonnet", args[args.IndexOf("--model") + 1]);
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
    public async Task GetResponseAsync_setsPerRunConfigDir_andForwardsToken()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet", ApiKey = "tok-1" }, stub);

        await client.GetResponseAsync(Messages());

        var env = stub.LastSpec!.Environment!;
        Assert.Equal("tok-1", env["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.False(string.IsNullOrWhiteSpace(env["CLAUDE_CONFIG_DIR"]));
    }

    [Fact]
    public async Task GetResponseAsync_usesFreshConfigDir_perRun()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var client = new ClaudeCodeChatClient(new AiOptions { Provider = AiProvider.ClaudeCode }, stub);

        await client.GetResponseAsync(Messages());
        await client.GetResponseAsync(Messages());

        Assert.NotEqual(stub.Specs[0].Environment!["CLAUDE_CONFIG_DIR"],
                        stub.Specs[1].Environment!["CLAUDE_CONFIG_DIR"]);
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

    [Fact]
    public async Task GetResponseAsync_fencedEmptyResult_throws()
    {
        // Ein Fence-Block ohne Inhalt muss ebenfalls als leer erkannt werden (fail-closed).
        var client = Client(_ => new ProcessResult(0, Envelope("```json\n```"), ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));
    }

    [Fact]
    public async Task GetResponseAsync_mapsUsageTokens_fromEnvelope()
    {
        var client = Client(_ => new ProcessResult(0, EnvelopeWithUsage("{\"x\":1}", 1200, 340), ""));

        var response = await client.GetResponseAsync(Messages());

        Assert.NotNull(response.Usage);
        Assert.Equal(1200, response.Usage!.InputTokenCount);
        Assert.Equal(340, response.Usage.OutputTokenCount);
    }

    [Fact]
    public async Task GetResponseAsync_leavesUsageNull_whenEnvelopeHasNoUsage()
    {
        // Provider ohne Usage-Meldung: Audit soll null (nicht 0) sehen, kein erfundener Verbrauch.
        var client = Client(_ => new ProcessResult(0, Envelope("OK"), ""));

        var response = await client.GetResponseAsync(Messages());

        Assert.Null(response.Usage);
    }

    [Fact]
    public async Task GetResponseAsync_mcpDisabled_keepsTodaysArgs()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub,
            new Naudit.Infrastructure.Mcp.McpOptions { Enabled = false });

        await client.GetResponseAsync(Messages());

        var args = stub.LastSpec!.Arguments.ToList();
        Assert.Equal("1", args[args.IndexOf("--max-turns") + 1]);
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);   // Tools aus
        Assert.DoesNotContain("--mcp-config", args);
        Assert.DoesNotContain("--allowedTools", args);
    }

    [Fact]
    public async Task GetResponseAsync_mcpEnabled_addsMcpConfig_allowlist_andRaisesMaxTurns()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var mcp = new Naudit.Infrastructure.Mcp.McpOptions
        {
            Enabled = true,
            MaxIterations = 5,
            Servers =
            {
                new() { Name = "context7", Transport = "http", Url = "https://mcp.context7.com/mcp", ApiKey = "sk-1" },
            },
        };
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub, mcp);

        await client.GetResponseAsync(Messages());

        var args = stub.LastSpec!.Arguments.ToList();
        Assert.Equal("5", args[args.IndexOf("--max-turns") + 1]);         // Loop erlaubt
        Assert.Contains("--mcp-config", args);
        Assert.Contains("--allowedTools", args);
        // Allowlist enthält NUR das MCP-Tool des Servers — kein Bash/Edit/Read.
        var allow = args[args.IndexOf("--allowedTools") + 1];
        Assert.Contains("mcp__context7", allow);
        Assert.DoesNotContain("Bash", allow);
        Assert.DoesNotContain("Edit", allow);
        Assert.DoesNotContain("Read", allow);
        Assert.DoesNotContain("--tools", args);   // ersetzt durch die Allowlist
    }

    [Fact]
    public async Task GetResponseAsync_mcpEnabled_mcpConfigJson_containsServerUrl()
    {
        // --mcp-config trägt jetzt einen DATEIPFAD (nicht mehr JSON auf argv) — Inhalt im Responder lesen,
        // solange die Datei existiert (RunAsync läuft synchron, vor dem finally-Cleanup).
        string? capturedConfig = null;
        var stub = new StubProcessRunner(spec =>
        {
            var a = spec.Arguments.ToList();
            var i = a.IndexOf("--mcp-config");
            if (i >= 0) capturedConfig = File.ReadAllText(a[i + 1]);
            return new ProcessResult(0, Envelope("OK"), "");
        });
        var mcp = new Naudit.Infrastructure.Mcp.McpOptions
        {
            Enabled = true,
            Servers = { new() { Name = "context7", Transport = "http", Url = "https://mcp.context7.com/mcp", ApiKey = "sk-secret" } },
        };
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub, mcp);

        await client.GetResponseAsync(Messages());

        var args = stub.LastSpec!.Arguments.ToList();
        var configPath = args[args.IndexOf("--mcp-config") + 1];
        Assert.False(configPath.TrimStart().StartsWith('{'));   // Pfad, kein inline-JSON
        Assert.NotNull(capturedConfig);
        Assert.Contains("context7", capturedConfig!);
        Assert.Contains("https://mcp.context7.com/mcp", capturedConfig!);

        // Der ApiKey darf nirgends auf argv landen (ps/`/proc/<pid>/cmdline`-sichtbar).
        Assert.All(args, a => Assert.DoesNotContain("sk-secret", a));
    }

    [Fact]
    public async Task GetResponseAsync_mcpEnabled_mcpConfigFile_hasUserOnlyPermissions()
    {
        // Die Temp-Datei trägt den ApiKey im Klartext — muss ab Erzeugung (nicht erst nachträglich
        // per chmod) auf 0600 stehen. Perms im Responder lesen, solange die Datei noch existiert.
        UnixFileMode? capturedMode = null;
        var stub = new StubProcessRunner(spec =>
        {
            var a = spec.Arguments.ToList();
            var i = a.IndexOf("--mcp-config");
            if (i >= 0 && !OperatingSystem.IsWindows())
                capturedMode = File.GetUnixFileMode(a[i + 1]);
            return new ProcessResult(0, Envelope("OK"), "");
        });
        var mcp = new Naudit.Infrastructure.Mcp.McpOptions
        {
            Enabled = true,
            Servers = { new() { Name = "context7", Transport = "http", Url = "https://mcp.context7.com/mcp", ApiKey = "sk-secret" } },
        };
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub, mcp);

        await client.GetResponseAsync(Messages());

        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, capturedMode);
        else
            Assert.Null(capturedMode);
    }

    [Fact]
    public async Task GetResponseAsync_mcpServerNameWithSpace_throws()
    {
        // Ein Name mit Space würde in --allowedTools zu zusätzlichen, ungeprüften Tokens aufsplitten
        // (potenziell eingebaute Tools wieder freigeben) — fail-closed statt fail-open.
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var mcp = new Naudit.Infrastructure.Mcp.McpOptions
        {
            Enabled = true,
            Servers = { new() { Name = "c7 Bash", Transport = "http", Url = "https://mcp.context7.com/mcp" } },
        };
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub, mcp);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));
    }

    [Fact]
    public async Task GetResponseAsync_stdioServerWithoutCommand_throws_andLeavesNoTempFile()
    {
        // Ohne Command würde "command": null serialisiert — die CLI bräche beim Start dieses
        // Servers ab (Exit-Code != 0), was den ganzen Review scheitern lässt. Fail-closed statt dessen,
        // und zwar VOR dem Anlegen der Temp-Datei (kein leeres/halbes File soll liegen bleiben).
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var tempCountBefore = Directory.GetFiles(Path.GetTempPath(), "naudit-mcp-*.json").Length;
        var mcp = new Naudit.Infrastructure.Mcp.McpOptions
        {
            Enabled = true,
            Servers = { new() { Name = "local-tool", Transport = "stdio", Command = "" } },
        };
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub, mcp);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));

        Assert.Contains("local-tool", ex.Message);
        Assert.Contains("Command", ex.Message);
        Assert.Equal(tempCountBefore, Directory.GetFiles(Path.GetTempPath(), "naudit-mcp-*.json").Length);
    }

    [Fact]
    public async Task GetResponseAsync_httpServerWithoutUrl_throws_andLeavesNoTempFile()
    {
        // Analog: "url": null lässt den http-Transport der CLI ins Leere laufen. Auch dieser Fail-Pfad
        // (Finding 4) darf keine Temp-Datei hinterlassen — BuildMcpConfigJson wirft, bevor sie angelegt wird.
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var tempCountBefore = Directory.GetFiles(Path.GetTempPath(), "naudit-mcp-*.json").Length;
        var mcp = new Naudit.Infrastructure.Mcp.McpOptions
        {
            Enabled = true,
            Servers = { new() { Name = "context7", Transport = "http", Url = "" } },
        };
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub, mcp);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));

        Assert.Contains("context7", ex.Message);
        Assert.Contains("Url", ex.Message);
        Assert.Equal(tempCountBefore, Directory.GetFiles(Path.GetTempPath(), "naudit-mcp-*.json").Length);
    }

    // --- Härtung: keine eingebauten Tools, leeres CWD, minimale Umgebung ---

    private static Naudit.Infrastructure.Mcp.McpOptions Context7() => new()
    {
        Enabled = true,
        Servers = { new() { Name = "context7", Transport = "http", Url = "https://mcp.context7.com/mcp" } },
    };

    [Fact]
    public async Task GetResponseAsync_mcpEnabled_disallowsAllBuiltInTools_andIgnoresAmbientMcpConfig()
    {
        // --allowedTools allein entfernt die eingebauten Tools NICHT (Read/Grep/Glob brauchen in -p keine
        // Freigabe) — ein Diff mit Prompt-Injection könnte sonst Dateien neben dem CWD lesen.
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub, Context7());

        await client.GetResponseAsync(Messages());

        var args = stub.LastSpec!.Arguments.ToList();
        Assert.Contains("--disallowedTools", args);
        var deny = args[args.IndexOf("--disallowedTools") + 1].Split(' ');
        foreach (var tool in new[] { "Bash", "Read", "Edit", "Write", "Glob", "Grep", "WebFetch", "WebSearch", "NotebookEdit", "Task" })
            Assert.Contains(tool, deny);
        Assert.DoesNotContain(deny, t => t.StartsWith("mcp__", StringComparison.Ordinal));
        Assert.Contains("--strict-mcp-config", args);
    }

    [Fact]
    public async Task GetResponseAsync_runsInDedicatedEmptyWorkingDirectory_notTmp()
    {
        string? cwd = null;
        var entries = -1;
        var stub = new StubProcessRunner(spec =>
        {
            cwd = spec.WorkingDirectory;
            entries = Directory.EnumerateFileSystemEntries(spec.WorkingDirectory!).Count();
            return new ProcessResult(0, Envelope("OK"), "");
        });
        var client = new ClaudeCodeChatClient(new AiOptions { Provider = AiProvider.ClaudeCode }, stub);

        await client.GetResponseAsync(Messages());

        Assert.NotNull(cwd);
        Assert.NotEqual(Path.GetFullPath(Path.GetTempPath()).TrimEnd('/'), Path.GetFullPath(cwd!).TrimEnd('/'));
        Assert.Equal(0, entries);              // leer: nichts, was ein Tool lesen könnte
        Assert.False(Directory.Exists(cwd));   // nach dem Lauf aufgeräumt
    }

    [Fact]
    public async Task GetResponseAsync_masksInheritedSecrets_butKeepsPathAndHome()
    {
        const string secretVar = "Naudit__GitHub__Token_CCTEST";
        Environment.SetEnvironmentVariable(secretVar, "ghp-geheim");
        try
        {
            var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
            var client = new ClaudeCodeChatClient(
                new AiOptions { Provider = AiProvider.ClaudeCode, ApiKey = "tok-1" }, stub);

            await client.GetResponseAsync(Messages());

            var spec = stub.LastSpec!;
            var env = spec.Environment!;
            var inherited = spec.InheritedEnvironment!;                    // Minimal-Env-Allowlist des Runners
            Assert.False(env.ContainsKey(secretVar));
            Assert.False(ProcessEnvironment.IsAllowed(secretVar, inherited)); // nicht geerbt
            Assert.True(ProcessEnvironment.IsAllowed("PATH", inherited));
            Assert.True(ProcessEnvironment.IsAllowed("HOME", inherited));
            Assert.Equal("tok-1", env["CLAUDE_CODE_OAUTH_TOKEN"]);
            Assert.False(string.IsNullOrWhiteSpace(env["CLAUDE_CONFIG_DIR"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretVar, null);
        }
    }

    [Theory]
    [InlineData("PATH", true)]
    [InlineData("HOME", true)]
    [InlineData("HTTPS_PROXY", true)]
    [InlineData("no_proxy", true)]
    [InlineData("SSL_CERT_FILE", true)]
    [InlineData("ANTHROPIC_API_KEY", true)]
    [InlineData("CLAUDE_CODE_OAUTH_TOKEN", true)]
    [InlineData("Naudit__GitHub__Token", false)]
    [InlineData("Naudit__Ai__ApiKey", false)]
    [InlineData("ConnectionStrings__Default", false)]
    [InlineData("AWS_SECRET_ACCESS_KEY", false)]
    [InlineData("CLAUDE_CONFIG_DIR", false)]   // setzt der Adapter pro Lauf selbst
    public void IsPassThroughEnvironmentVariable_allowsOnlyCliEssentials(string name, bool expected)
        => Assert.Equal(expected, ClaudeCodeChatClient.IsPassThroughEnvironmentVariable(name));

    [Fact]
    public async Task MaskedEnvironment_doesNotReachRealChildProcess()
    {
        // End-to-end über den echten SystemProcessRunner: ein maskierter (null) Eintrag darf den
        // Kindprozess nicht mit seinem Wert erreichen. POSIX-only (/bin/sh).
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())) return;
        const string secretVar = "NAUDIT_CCTEST_SECRET";
        Environment.SetEnvironmentVariable(secretVar, "s3cret-wert");
        try
        {
            var env = new Dictionary<string, string?> { [secretVar] = null };
            var spec = new ProcessSpec("/bin/sh", ["-c", $"printf '%s' \"${secretVar}\""], null, env, null, TimeSpan.FromSeconds(10));

            var result = await new SystemProcessRunner().RunAsync(spec);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("s3cret-wert", result.StdOut);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretVar, null);
        }
    }

    [Fact]
    public async Task GetResponseAsync_mcpMaxIterationsZeroOrNegative_clampsToOne_inMaxTurnsArg()
    {
        // Naudit:Review:Mcp:MaxIterations=0 (oder negativ) darf --max-turns nicht ungültig machen /
        // den Tool-Loop komplett abschalten — Untergrenze 1 statt einem harten Review-Abbruch.
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var mcp = new Naudit.Infrastructure.Mcp.McpOptions
        {
            Enabled = true,
            MaxIterations = 0,
            Servers = { new() { Name = "context7", Transport = "http", Url = "https://mcp.context7.com/mcp" } },
        };
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub, mcp);

        await client.GetResponseAsync(Messages());

        var args = stub.LastSpec!.Arguments.ToList();
        Assert.Equal("1", args[args.IndexOf("--max-turns") + 1]);
    }
}
