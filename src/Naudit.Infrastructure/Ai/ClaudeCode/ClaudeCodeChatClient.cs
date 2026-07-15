using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Naudit.Infrastructure.Mcp;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>
/// IChatClient-Adapter, der die `claude` CLI headless aufruft (Abo-Auth statt API-Key).
/// Reiner Single-Shot: System-Prompt überschrieben, Diff über stdin, Tools aus, ein Turn.
/// MCP optional (mcp): Server per --mcp-config registriert, --allowedTools NUR auf die
/// MCP-Server beschränkt (kein Bash/Edit/Read/Write), --max-turns auf McpOptions.MaxIterations erhöht.
/// </summary>
public sealed class ClaudeCodeChatClient(AiOptions aiOptions, IProcessRunner runner, McpOptions? mcp = null) : IChatClient
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

        // ChatOptions (z. B. ResponseFormat=Json) hat in der CLI kein Äquivalent: JSON wird allein über
        // den System-Prompt erzwungen; --output-format json betrifft nur das Envelope, nicht den Modelltext.
        // MCP an ⇒ Loop erlauben (--max-turns N), Server registrieren (--mcp-config) und AUSSCHLIESSLICH
        // die MCP-Tools freigeben (--allowedTools mcp__<server>) — die eingebauten Datei-/Shell-Tools
        // (Bash/Edit/Read/Write) bleiben aus. MCP aus ⇒ exakt die heutigen Args (--tools "", --max-turns 1).
        var mcpEnabled = mcp is { Enabled: true, Servers.Count: > 0 };
        var args = new List<string> { "-p", "--output-format", "json" };
        string? mcpConfigPath = null;
        if (mcpEnabled)
        {
            // Fail-closed: ein Server-Name mit Space/Sonderzeichen würde die --allowedTools-Allowlist
            // aufsprengen (mehr Tokens als beabsichtigt) und könnte so eingebaute Tools wieder freigeben.
            foreach (var s in mcp!.Servers)
                if (!IsValidServerName(s.Name))
                    throw new InvalidOperationException(
                        $"MCP-Server-Name '{s.Name}' ist ungültig — erlaubt sind nur [A-Za-z0-9_-].");

            // Das --mcp-config-JSON enthält den ApiKey im Klartext (Authorization-Bearer-Header) — darf
            // NICHT auf argv landen (sichtbar via ps/`/proc/<pid>/cmdline`). Stattdessen in eine 0600-Temp-
            // Datei schreiben und nur den PFAD an die CLI übergeben; die Datei lebt für die Dauer des
            // CLI-Laufs und wird danach im finally-Block best-effort gelöscht.
            mcpConfigPath = Path.Combine(Path.GetTempPath(), $"naudit-mcp-{Guid.NewGuid():N}.json");
            // BuildMcpConfigJson (kann bei fehlendem Command/Url werfen, s. u.) VOR dem Anlegen der Datei
            // aufrufen — eine ungültige Server-Config erzeugt so gar nicht erst eine (leere) Temp-Datei.
            var mcpConfigJson = BuildMcpConfigJson(mcp);
            var fileOpts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                fileOpts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;   // 0600 schon bei Erzeugung
            try
            {
                await using (var fs = new FileStream(mcpConfigPath, fileOpts))
                await using (var w = new StreamWriter(fs))
                    await w.WriteAsync(mcpConfigJson);
            }
            catch
            {
                // Die Datei kann bereits (leer oder halb geschrieben, inkl. ApiKey) angelegt worden sein,
                // bevor der Fehler auftrat (z. B. Schreibfehler nach erfolgreichem CreateNew) — der äußere
                // finally-Block greift hier noch nicht (der try/RunAsync-Block beginnt erst danach), also
                // hier selbst best-effort aufräumen. File.Delete ist ein No-Op, falls nie eine Datei entstand.
                try { File.Delete(mcpConfigPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }

            // Untergrenze 1: 0/negativ in der Config würde --max-turns ungültig machen bzw. der CLI
            // jede Tool-Runde verbieten (fail-closed wäre hier ein harter Review-Abbruch statt Degradation).
            var maxTurns = Math.Max(1, mcp.MaxIterations);
            args.Add("--max-turns");
            args.Add(maxTurns.ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Add("--mcp-config");
            args.Add(mcpConfigPath);
            args.Add("--allowedTools");
            args.Add(string.Join(" ", mcp.Servers.Select(s => $"mcp__{s.Name}")));   // nur die MCP-Server
        }
        else
        {
            args.Add("--max-turns");
            args.Add("1");
            args.Add("--tools");
            args.Add("");   // Tools aus (heutiges Verhalten)
        }
        // --model IMMER als letztes Flag vor --system-prompt anhängen, in beiden Zweigen — damit der
        // MCP-aus-Pfad byte-identisch zum Stand vor diesem Feature bleibt.
        args.Add("--model");
        args.Add(model);
        // ReviewService liefert immer einen nicht-leeren System-Prompt (DefaultSystemPrompt). Fehlte er,
        // fiele claude auf seine Default-Coding-Agent-Persona zurück (lieferte kein Review-JSON).
        if (!string.IsNullOrEmpty(system))
        {
            args.Add("--system-prompt"); // ersetzt den GESAMTEN System-Prompt (kein Coding-Agent, kein CLAUDE.md)
            args.Add(system);
        }

        // Isolation pro Lauf: eigenes CLAUDE_CONFIG_DIR, damit parallele Läufe mit unterschiedlichen
        // Tokens (Autor-Sessions) nie CLI-State teilen. Token optional aus der Config.
        var configDir = Path.Combine(Path.GetTempPath(), "naudit-claude", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDir);
        var env = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = configDir };
        if (!string.IsNullOrWhiteSpace(aiOptions.ApiKey))
            env["CLAUDE_CODE_OAUTH_TOKEN"] = aiOptions.ApiKey;

        var spec = new ProcessSpec(
            FileName: "claude",
            Arguments: args,
            StdIn: user,
            Environment: env,
            WorkingDirectory: Path.GetTempPath(), // neutrales CWD: kein ambient CLAUDE.md
            Timeout: TimeSpan.FromSeconds(aiOptions.TimeoutSeconds));

        try
        {
            var result = await runner.RunAsync(spec, cancellationToken);

            if (result.ExitCode != 0)
                throw new InvalidOperationException(
                    $"claude beendete mit Exit-Code {result.ExitCode}. stderr: {result.StdErr}");

            var envelope = JsonSerializer.Deserialize<ClaudeResult>(result.StdOut, JsonOpts)
                ?? throw new InvalidOperationException("claude lieferte kein parsebares JSON-Envelope.");

            if (envelope.IsError || envelope.Subtype != "success")
                throw new InvalidOperationException(
                    $"claude meldete einen Fehler (subtype='{envelope.Subtype}'). stderr: {result.StdErr}");

            // Fences zuerst entfernen, damit ein leerer Fence-Block als leer erkannt wird (fail-closed).
            var text = StripJsonFences(envelope.Result ?? "");
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("claude lieferte ein leeres 'result'.");

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                // Token-Verbrauch aus dem CLI-Envelope ins MEAI-Usage heben, damit das Audit
                // (ChatResponse.Usage) den Abo-Weg genauso zählt wie den API-Provider. Fehlt usage
                // (Provider meldet keins), bleibt Usage null — kein erfundener 0-Verbrauch.
                Usage = envelope.Usage is { } u && (u.InputTokens is not null || u.OutputTokens is not null)
                    ? new UsageDetails { InputTokenCount = u.InputTokens, OutputTokenCount = u.OutputTokens }
                    : null,
            };
        }
        finally
        {
            // Best-effort beide Scratch-Ressourcen aufräumen: das per-Lauf-CLAUDE_CONFIG_DIR
            // (Autor-Sessions) und die --mcp-config-Temp-Datei mit dem ApiKey (MCP). Löschfehler
            // (z. B. bereits weg) sind egal — Cancellation wird hier NICHT abgefangen.
            try { Directory.Delete(configDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (mcpConfigPath is not null)
            {
                try { File.Delete(mcpConfigPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    // Nur [A-Za-z0-9_-] — der Name landet 1:1 (als "mcp__<name>") in der --allowedTools-Allowlist;
    // ein Space/Sonderzeichen würde zusätzliche, ungeprüfte Allowlist-Tokens einschleusen.
    private static bool IsValidServerName(string name)
        => System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[A-Za-z0-9_-]+\z");

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

    // Baut das Claude-Code --mcp-config-JSON aus der geteilten McpOptions-Config.
    // http ⇒ { "type":"http", "url":..., "headers": { "Authorization":"Bearer <key>" } };
    // stdio ⇒ { "command":..., "args":[...] }.
    private static string BuildMcpConfigJson(McpOptions mcp)
    {
        var servers = new Dictionary<string, object>();
        foreach (var s in mcp.Servers)
        {
            if (string.Equals(s.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
            {
                // Fail-closed: ein fehlender Command würde als "command": null serialisiert — die CLI
                // bricht dann beim Start dieses MCP-Servers ab (Exit-Code != 0), was den GANZEN Review
                // scheitern lässt. Lieber hier klar und früh melden statt den CLI-Fehler zu diagnostizieren.
                if (string.IsNullOrWhiteSpace(s.Command))
                    throw new InvalidOperationException($"MCP-Server '{s.Name}': Command fehlt (stdio-Transport).");
                servers[s.Name] = new Dictionary<string, object?>
                {
                    ["command"] = s.Command,
                    ["args"] = s.Arguments ?? new List<string>(),
                };
            }
            else
            {
                // Analog: "url": null lässt den http-Transport der CLI ins Leere laufen.
                if (string.IsNullOrWhiteSpace(s.Url))
                    throw new InvalidOperationException($"MCP-Server '{s.Name}': Url fehlt (http-Transport).");
                var entry = new Dictionary<string, object?> { ["type"] = "http", ["url"] = s.Url };
                if (!string.IsNullOrWhiteSpace(s.ApiKey))
                    entry["headers"] = new Dictionary<string, string> { ["Authorization"] = $"Bearer {s.ApiKey}" };
                servers[s.Name] = entry;
            }
        }
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["mcpServers"] = servers });
    }

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
        [property: JsonPropertyName("result")] string? Result,
        [property: JsonPropertyName("usage")] ClaudeUsage? Usage);

    // usage-Teilobjekt des Envelopes; nur Input/Output (Cache-Felder ignoriert der Adapter).
    private sealed record ClaudeUsage(
        [property: JsonPropertyName("input_tokens")] long? InputTokens,
        [property: JsonPropertyName("output_tokens")] long? OutputTokens);
}
