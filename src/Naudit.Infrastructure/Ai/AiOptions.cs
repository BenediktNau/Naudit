namespace Naudit.Infrastructure.Ai;

public enum AiProvider { Anthropic, Ollama, OpenAICompatible }

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
}
