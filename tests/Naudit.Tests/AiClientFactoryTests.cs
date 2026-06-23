using Microsoft.Extensions.AI;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.ClaudeCode;
using OllamaSharp;
using Xunit;

namespace Naudit.Tests;

public class AiClientFactoryTests
{
    [Fact]
    public void Create_ollama_returnsChatClient()
    {
        var client = AiClientFactory.Create(new AiOptions
        {
            Provider = AiProvider.Ollama,
            Model = "llama3.1",
            Endpoint = "http://localhost:11434",
        });

        Assert.IsType<OllamaApiClient>(client);
    }

    [Fact]
    public void Create_anthropic_withoutApiKey_throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            AiClientFactory.Create(new AiOptions { Provider = AiProvider.Anthropic, Model = "claude-sonnet-4-6" }));
        Assert.Contains("ApiKey", ex.Message);
    }

    [Fact]
    public void Create_openAICompatible_withoutApiKey_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AiClientFactory.Create(new AiOptions { Provider = AiProvider.OpenAICompatible, Model = "gpt-4o" }));
    }

    [Fact]
    public void Create_claudeCode_returnsClaudeCodeChatClient()
    {
        // Kein ApiKey nötig — Auth läuft über die Umgebung (CLAUDE_CODE_OAUTH_TOKEN).
        var client = AiClientFactory.Create(new AiOptions
        {
            Provider = AiProvider.ClaudeCode,
            Model = "sonnet",
        });

        Assert.IsType<ClaudeCodeChatClient>(client);
    }
}
