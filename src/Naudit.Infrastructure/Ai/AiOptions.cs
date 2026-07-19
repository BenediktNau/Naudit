namespace Naudit.Infrastructure.Ai;

public enum AiProvider { Anthropic, Ollama, OpenAICompatible, ClaudeCode }

/// <summary>Session-Routing pro Review: globaler Provider | autor-gebunden | Round-Robin-Pool.</summary>
public enum SessionRouting { Single, Author, RoundRobin }

/// <summary>Wo Abo-Session-Läufe (Author/RoundRobin) ausgeführt werden: in-process (heutiges
/// Verhalten) oder in Geschwister-Containern pro Account über den Host-Docker-Socket.</summary>
public enum SessionSandbox { None, Docker }

public sealed class AiOptions
{
    public AiProvider Provider { get; set; } = AiProvider.Ollama;
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }
    /// <summary>Ollama-Base-URL oder Base-URL eines OpenAI-kompatiblen Dienstes (z. B. NVIDIA).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Gesamt-Timeout einer LLM-Anfrage in Sekunden. Default 600 (10 min): große Diffs
    /// bzw. Thinking-Modelle (z. B. Ollama qwen3.5) sprengen sonst den HttpClient-Standard von 100 s.</summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>Wie der Chat-Client pro Review gewählt wird (Naudit:Ai:SessionRouting).
    /// Single = globaler Provider (Default, heutiges Verhalten); Author = autor-gebunden;
    /// RoundRobin = Opt-in-Pool rundlaufend.</summary>
    public SessionRouting SessionRouting { get; set; } = SessionRouting.Single;

    /// <summary>Naudit:Ai:SessionSandbox — Default None = heutiger In-Process-Runner. Docker greift
    /// nur für Author-/RoundRobin-Routing; im Single-Modus (globaler Provider) ist es bedeutungslos.
    /// Sub-Optionen unter Naudit:Ai:Sandbox (SessionSandboxOptions).</summary>
    public SessionSandbox SessionSandbox { get; set; } = SessionSandbox.None;
}
