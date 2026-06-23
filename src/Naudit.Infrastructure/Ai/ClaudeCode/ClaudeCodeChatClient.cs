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
