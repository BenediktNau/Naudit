using Microsoft.Extensions.AI;

namespace Naudit.Tests.Fakes;

internal sealed class FakeChatClient(string responseText) : IChatClient
{
    public List<ChatMessage>? LastMessages { get; private set; }
    public ChatOptions? LastOptions { get; private set; }
    public int CallCount { get; private set; }

    /// <summary>Optionales Usage an der Antwort — der ClaudeCode-Adapter füllt es real.</summary>
    public UsageDetails? Usage { get; set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        LastOptions = options;
        CallCount++;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)) { Usage = Usage });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
