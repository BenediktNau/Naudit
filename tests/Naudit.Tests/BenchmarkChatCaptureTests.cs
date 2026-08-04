using Microsoft.Extensions.AI;
using Naudit.Benchmark;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Tests.Fakes;

namespace Naudit.Tests;

/// <summary>Der IChatClient-Dekorator ist die einzige Spur der drei stillen Ausfallpfade, die
/// niemand loggt: Profil-Destillation ohne Workspace, Destillation ohne Quelldokumente und eine
/// leer gebliebene Kontextsammlung (WorkspaceContextCollector hat nicht einmal einen Logger).
/// Die Prompts entstehen hier bewusst über den echten PromptBuilder — ändert sich dort eine
/// Abschnittsüberschrift, fallen diese Tests um statt der Benchmark-Zahl.</summary>
public class BenchmarkChatCaptureTests
{
    private static ReviewRequest Request() => new("getsentry/sentry", 93824, "Titel");

    private static IReadOnlyList<CodeChange> Changes() => [new CodeChange("a.cs", "@@ -1 +1 @@\n+var x = 1;")];

    private static ReviewContext FullContext() => new(
        [new FileEnvironment("a.cs", 1, "var x = 1;", IsFullFile: true)], [], "Ein Repo.");

    private static IList<ChatMessage> ReviewPrompt(ReviewContext? context, string? guidelines)
        => PromptBuilder.Build(PromptBuilder.DefaultSystemPrompt, Request(), Changes(),
            findings: [], context: context, memory: [], toolsAvailable: false, guidelines: guidelines);

    /// <summary>So sieht der Destillations-Aufruf aus: eigener System-Prompt, im User-Teil nur die
    /// Repo-Dokumente — keine einzige PromptBuilder-Überschrift.</summary>
    private static IList<ChatMessage> DistillPrompt() =>
    [
        new(ChatRole.System, "Distill an architecture profile from the documentation below."),
        new(ChatRole.User, "## README.md\nEin Projekt mit Dokumentation.\n\n## docs/arch.md\nSchichten.\n"),
    ];

    [Fact]
    public async Task Vollstaendiger_Review_Prompt_vermerkt_Kontext_Guidelines_und_Tokens()
    {
        var inner = new FakeChatClient("{}") { Usage = new UsageDetails { InputTokenCount = 1234, OutputTokenCount = 56 } };
        var capture = new ReviewCapture();
        var sut = new CapturingChatClient(inner, capture);

        await sut.GetResponseAsync(ReviewPrompt(FullContext(), "## Schichten\nCore kennt kein SDK."));

        Assert.True(capture.ReviewPromptSeen);
        Assert.True(capture.ContextInPrompt);
        Assert.True(capture.GuidelinesInPrompt);
        Assert.Equal(1234, capture.InputTokens);
        Assert.Equal(56, capture.OutputTokens);
    }

    [Fact]
    public async Task Degradierter_Review_Prompt_meldet_fehlenden_Kontext_und_fehlende_Guidelines()
    {
        // Genau der fail-open-Fall: Checkout/Destillation gescheitert ⇒ beide Sektionen fehlen,
        // das Review läuft diff-only weiter und sähe im Ergebnis nur wie ein schwächeres aus.
        var inner = new FakeChatClient("{}");
        var capture = new ReviewCapture();
        var sut = new CapturingChatClient(inner, capture);

        await sut.GetResponseAsync(ReviewPrompt(ReviewContext.Empty, guidelines: null));

        Assert.True(capture.ReviewPromptSeen);
        Assert.False(capture.ContextInPrompt);
        Assert.False(capture.GuidelinesInPrompt);
        Assert.Null(capture.InputTokens);
    }

    [Fact]
    public async Task Destillations_Aufruf_wird_nicht_als_Review_gewertet()
    {
        var inner = new FakeChatClient("Profil");
        var capture = new ReviewCapture();
        var sut = new CapturingChatClient(inner, capture);

        await sut.GetResponseAsync(DistillPrompt());

        Assert.False(capture.ReviewPromptSeen);
        Assert.False(capture.ContextInPrompt);
        Assert.False(capture.GuidelinesInPrompt);
    }

    [Fact]
    public async Task Nach_Destillation_zaehlt_der_Review_Aufruf()
    {
        // Reihenfolge eines echten Reviews: erst destilliert DistillingReviewGuidelines über
        // DENSELBEN globalen IChatClient, dann läuft der Review-Aufruf. Gewertet wird der Review.
        var inner = new FakeChatClient("{}") { Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 } };
        var capture = new ReviewCapture();
        var sut = new CapturingChatClient(inner, capture);

        await sut.GetResponseAsync(DistillPrompt());
        await sut.GetResponseAsync(ReviewPrompt(FullContext(), "## Schichten"));

        Assert.Equal(2, inner.CallCount);
        Assert.True(capture.ContextInPrompt);
        Assert.True(capture.GuidelinesInPrompt);
        Assert.Equal(10, capture.InputTokens);
    }

    [Fact]
    public async Task Antwort_und_Optionen_werden_unveraendert_durchgereicht()
    {
        var inner = new FakeChatClient("{\"summary\":\"ok\"}");
        var sut = new CapturingChatClient(inner, new ReviewCapture());
        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };

        var response = await sut.GetResponseAsync(ReviewPrompt(FullContext(), null), options);

        Assert.Equal("{\"summary\":\"ok\"}", response.Text);
        Assert.Same(options, inner.LastOptions);
    }

    [Fact]
    public void Reset_loescht_die_Prompt_Diagnose()
    {
        var capture = new ReviewCapture();
        capture.RecordReviewPrompt(contextInPrompt: true, guidelinesInPrompt: true, inputTokens: 1, outputTokens: 2);

        capture.Reset();

        Assert.False(capture.ReviewPromptSeen);
        Assert.False(capture.ContextInPrompt);
        Assert.False(capture.GuidelinesInPrompt);
        Assert.Null(capture.InputTokens);
        Assert.Null(capture.OutputTokens);
    }
}
