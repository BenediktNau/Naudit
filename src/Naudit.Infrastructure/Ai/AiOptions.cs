namespace Naudit.Infrastructure.Ai;

public enum AiProvider { Anthropic, Ollama, OpenAICompatible }

public sealed class AiOptions
{
    public AiProvider Provider { get; set; } = AiProvider.Ollama;
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }
    /// <summary>Ollama-Base-URL oder Base-URL eines OpenAI-kompatiblen Dienstes (z. B. NVIDIA).</summary>
    public string? Endpoint { get; set; }
}
