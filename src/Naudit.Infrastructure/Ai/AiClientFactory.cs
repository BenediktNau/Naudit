using System.ClientModel;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace Naudit.Infrastructure.Ai;

public static class AiClientFactory
{
    public static IChatClient Create(AiOptions options)
    {
        switch (options.Provider)
        {
            case AiProvider.Ollama:
                var baseUrl = string.IsNullOrWhiteSpace(options.Endpoint) ? "http://localhost:11434" : options.Endpoint;
                // Eigener HttpClient: der OllamaApiClient-Default-HttpClient hat 100 s Timeout — zu kurz
                // für große Diffs / Thinking-Modelle. BaseAddress ist beim HttpClient-Ctor Pflicht.
                var ollamaHttp = new HttpClient
                {
                    BaseAddress = new Uri(baseUrl),
                    Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
                };
                return new OllamaApiClient(ollamaHttp, options.Model);

            case AiProvider.Anthropic:
                RequireApiKey(options, "Anthropic");
                return new Anthropic.SDK.AnthropicClient(options.ApiKey!).Messages;

            case AiProvider.OpenAICompatible:
                RequireApiKey(options, "OpenAICompatible");
                return CreateOpenAICompatible(options);

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Provider, "Unknown AI provider.");
        }
    }

    private static IChatClient CreateOpenAICompatible(AiOptions options)
    {
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
            clientOptions.Endpoint = new Uri(options.Endpoint);

        var client = new OpenAIClient(new ApiKeyCredential(options.ApiKey!), clientOptions);
        return client.GetChatClient(options.Model).AsIChatClient();
    }

    private static void RequireApiKey(AiOptions options, string provider)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException($"ApiKey is required for the {provider} provider.", nameof(options));
    }
}
