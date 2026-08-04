using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Naudit.Benchmark;

/// <summary>Reicht jeden LLM-Aufruf unverändert durch und hält je Review fest, was der Prompt
/// tatsächlich enthielt. Das ist der einzige Weg, drei stille fail-open-Pfade sichtbar zu machen,
/// die niemand loggt: eine Profil-Destillation ohne Workspace, eine ohne gefundene Quelldokumente,
/// und eine leer gebliebene Kontextsammlung (WorkspaceContextCollector hat nicht einmal einen
/// Logger). Alle drei ergeben ein diff-only-Review, das im Ergebnis nur wie ein schwächeres
/// Review aussieht.
///
/// <para><b>Destillation vs. Review:</b> die Profil-Destillation (DistillingReviewGuidelines) ruft
/// DENSELBEN globalen IChatClient auf. Gewertet wird nur ein Aufruf, dessen User-Text die beiden
/// Überschriften trägt, die PromptBuilder.Build IMMER schreibt — die Merge-Request-Zeile und die
/// Grounding-Sektion. Der Destillations-Prompt besteht ausschließlich aus Repo-Dokumenten und
/// kann beide nicht zugleich führen. Der letzte solche Aufruf gewinnt, denn der Review-Aufruf
/// kommt in ReviewService nach dem Grounding.</para>
///
/// <para>Der System-Prompt wird bewusst NICHT durchsucht: er erwähnt "Repository context" und
/// "Project guidelines" in Anführungszeichen und würde jede Prüfung wahr machen.</para></summary>
public sealed class CapturingChatClient(IChatClient inner, ReviewCapture capture) : IChatClient
{
    // Wörtlich aus src/Naudit.Core/Review/PromtBuilder.cs (Dateiname mit Tippfehler, Klasse heißt
    // PromptBuilder). Ändert sich dort eine Überschrift, fallen die Tests um — nicht die Zahl.
    /// <summary>Der umhüllte, echte Client — für den Verdrahtungstest.</summary>
    public IChatClient Inner => inner;

    public const string MergeRequestHeading = "# Merge Request: ";
    public const string FindingsHeading = "# Static-analysis & dependency findings";
    public const string ContextHeading = "# Repository context (read-only grounding from the checked-out repo)";
    public const string GuidelinesHeading = "# Project guidelines (distilled from this repository's own documentation; maintainer-curated, authoritative)";

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();   // einmal materialisieren: wir lesen UND reichen weiter
        var userText = string.Concat(list.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        var isReviewPrompt = userText.Contains(MergeRequestHeading, StringComparison.Ordinal)
                          && userText.Contains(FindingsHeading, StringComparison.Ordinal);

        var response = await inner.GetResponseAsync(list, options, cancellationToken);

        if (isReviewPrompt)
            capture.RecordReviewPrompt(
                contextInPrompt: userText.Contains(ContextHeading, StringComparison.Ordinal),
                guidelinesInPrompt: userText.Contains(GuidelinesHeading, StringComparison.Ordinal),
                inputTokens: response.Usage?.InputTokenCount,
                outputTokens: response.Usage?.OutputTokenCount);

        return response;
    }

    // ReviewService nutzt nur die non-streaming Variante; dünner Wrapper wie im FallbackChatClient.
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : inner.GetService(serviceType, serviceKey);

    // Der innere Client wird in der Dekorations-Fabrik erzeugt und daher NICHT vom Container
    // verfolgt — er gehört uns und wird hier mit entsorgt.
    public void Dispose() => inner.Dispose();
}
