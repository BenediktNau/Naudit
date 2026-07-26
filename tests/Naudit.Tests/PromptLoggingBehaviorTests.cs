using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Ai.Logging;
using Naudit.Tests.Fakes;

namespace Naudit.Tests;

/// <summary>Die Prompt-/Kommunikations-Middleware: erfasst Prompt + Antwort + Metadaten pro
/// LLM-Aufruf, persistiert nur mit Review-Korrelation, respektiert IncludePrompts/Response und
/// reicht echte Fehler nach der Erfassung weiter.</summary>
public class PromptLoggingBehaviorTests
{
    private sealed class RecordingSink : IChatTranscriptSink
    {
        public ChatTranscript? Last { get; private set; }
        public int Count { get; private set; }

        public Task RecordAsync(ChatTranscript t, CancellationToken ct = default)
        {
            Last = t;
            Count++;
            return Task.CompletedTask;
        }
    }

    private static (PromptLoggingBehavior behavior, RecordingSink sink) Build(AiLoggingOptions options, ReviewCorrelation? corr)
    {
        var sink = new RecordingSink();
        var services = new ServiceCollection();
        services.AddScoped<IChatTranscriptSink>(_ => sink);
        var sp = services.BuildServiceProvider();
        var accessor = new AsyncLocalReviewCorrelationAccessor { Current = corr };
        var behavior = new PromptLoggingBehavior(
            options, accessor, sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PromptLoggingBehavior>.Instance);
        return (behavior, sink);
    }

    private static ChatCompletionRequest Req() => new(
        new FakeChatClient("unused"),
        new List<ChatMessage> { new(ChatRole.System, "SYS"), new(ChatRole.User, "USER-DIFF") },
        new ChatOptions());

    [Fact]
    public async Task Handle_persistiert_Transcript_mit_Prompts_und_Usage()
    {
        var corr = new ReviewCorrelation(Guid.NewGuid(), "owner/repo", 7, "Webhook");
        var (behavior, sink) = Build(
            new AiLoggingOptions { Enabled = true, Persist = true, IncludePrompts = true, IncludeResponse = true }, corr);

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "LLM-ANSWER"))
        {
            ModelId = "test-model",
            Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 3 },
        };
        var result = await behavior.Handle(Req(),
            (_, _) => new ValueTask<ChatResponse>(response), CancellationToken.None);

        Assert.Same(response, result);
        Assert.Equal(1, sink.Count);
        Assert.Equal(corr.Id, sink.Last!.CorrelationId);
        Assert.Equal("owner/repo", sink.Last.ProjectId);
        Assert.Equal("SYS", sink.Last.SystemPrompt);
        Assert.Equal("USER-DIFF", sink.Last.UserPrompt);
        Assert.Equal("LLM-ANSWER", sink.Last.ResponseText);
        Assert.Equal(11L, sink.Last.InputTokens);
        Assert.Equal(3L, sink.Last.OutputTokens);
        Assert.False(sink.Last.Failed);
    }

    [Fact]
    public async Task Handle_ohne_Korrelation_persistiert_nicht_gibt_Antwort_zurueck()
    {
        var (behavior, sink) = Build(new AiLoggingOptions { Enabled = true, Persist = true }, corr: null);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "x"));

        var result = await behavior.Handle(Req(),
            (_, _) => new ValueTask<ChatResponse>(response), CancellationToken.None);

        Assert.Same(response, result);
        Assert.Equal(0, sink.Count);
    }

    [Fact]
    public async Task Handle_laesst_Prompt_Texte_weg_wenn_IncludePrompts_aus()
    {
        var corr = new ReviewCorrelation(Guid.NewGuid(), "o/r", 1, "Ci");
        var (behavior, sink) = Build(
            new AiLoggingOptions { Enabled = true, Persist = true, IncludePrompts = false, IncludeResponse = false }, corr);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "secret"));

        await behavior.Handle(Req(), (_, _) => new ValueTask<ChatResponse>(response), CancellationToken.None);

        Assert.Null(sink.Last!.SystemPrompt);
        Assert.Null(sink.Last.UserPrompt);
        Assert.Null(sink.Last.ResponseText);
    }

    [Fact]
    public async Task Handle_erfasst_Fehlversuch_und_reicht_Exception_weiter()
    {
        var corr = new ReviewCorrelation(Guid.NewGuid(), "o/r", 2, "Webhook");
        var (behavior, sink) = Build(
            new AiLoggingOptions { Enabled = true, Persist = true, IncludePrompts = true }, corr);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await behavior.Handle(Req(),
                (_, _) => throw new InvalidOperationException("boom"), CancellationToken.None));

        Assert.Equal(1, sink.Count);
        Assert.True(sink.Last!.Failed);
        Assert.Null(sink.Last.ResponseText);
    }
}
