using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class FallbackChatClientTests
{
    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Session kaputt");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatMessage[] Messages() => [new(ChatRole.User, "diff")];

    [Fact]
    public async Task AuthorSucceeds_attributesSession_andSkipsFallback()
    {
        var failures = 0;
        var client = new FallbackChatClient(new FakeChatClient("AUTOR"), new FakeChatClient("GLOBAL"),
            sessionAccountId: 7, onAuthorFailure: () => failures++, NullLogger.Instance);

        var response = await client.GetResponseAsync(Messages());

        Assert.Equal("AUTOR", response.Text);
        Assert.Equal(7, client.AnsweredBySessionAccountId);
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task AuthorFails_marksFailure_andFallsBackToGlobal()
    {
        var failures = 0;
        var client = new FallbackChatClient(new ThrowingChatClient(), new FakeChatClient("GLOBAL"),
            sessionAccountId: 7, onAuthorFailure: () => failures++, NullLogger.Instance);

        var response = await client.GetResponseAsync(Messages());

        Assert.Equal("GLOBAL", response.Text);
        Assert.Null(client.AnsweredBySessionAccountId);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task BothFail_throws_failClosed()
    {
        var client = new FallbackChatClient(new ThrowingChatClient(), new ThrowingChatClient(),
            sessionAccountId: 7, onAuthorFailure: () => { }, NullLogger.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));
    }

    [Fact]
    public async Task Cancellation_propagates_withoutFallback()
    {
        var failures = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FallbackChatClient(new ThrowingChatClient(), new FakeChatClient("GLOBAL"),
            sessionAccountId: 7, onAuthorFailure: () => failures++, NullLogger.Instance);

        // Abbruch ist kein Session-Fehler: kein Cooldown, kein Global-Lauf.
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync(Messages(), cancellationToken: cts.Token));
        Assert.Equal(0, failures);
    }
}
