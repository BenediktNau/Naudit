using System.Text.Json;
using Microsoft.Extensions.AI;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

public sealed class ReviewService(IChatClient chatClient, IGitPlatform gitPlatform, ReviewOptions options)
{
    // Web-Defaults: camelCase + case-insensitive — passt zu den JSON-Feldern summary/verdict.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var changes = await gitPlatform.GetChangesAsync(request, ct);
        if (changes.Count == 0)
            return new ReviewResult(string.Empty, ReviewVerdict.Approve);

        var messages = PromptBuilder.Build(options.SystemPrompt, request, changes);

        // Structured Output Core-rein: JSON-Mode (in MEAI.Abstractions), Deserialisierung selbst.
        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        var response = await chatClient.GetResponseAsync(messages, chatOptions, ct);

        var parsed = JsonSerializer.Deserialize<LlmReviewResponse>(response.Text, JsonOpts)
            ?? throw new InvalidOperationException("LLM lieferte keine parsebare Review-Antwort.");

        // Fail-closed: nur explizite Verdicts akzeptieren; alles andere (unbekannt, leer, null)
        // ist ein Fehler — sonst würde unklare/kaputte LLM-Ausgabe das Gate still auf approve fallen lassen.
        var verdict = parsed.Verdict?.ToLowerInvariant() switch
        {
            "request_changes" => ReviewVerdict.RequestChanges,
            "approve" => ReviewVerdict.Approve,
            _ => throw new InvalidOperationException($"Unerwartetes Verdict vom LLM: '{parsed.Verdict}'."),
        };

        await gitPlatform.PostSummaryAsync(request, parsed.Summary, ct);
        return new ReviewResult(parsed.Summary, verdict);
    }

    // Wire-DTO für die LLM-Antwort. Verdict bewusst als string (kein Enum),
    // um Enum-JSON-Fragilität zu vermeiden; Mapping erfolgt oben.
    private sealed record LlmReviewResponse(string Summary, string Verdict);
}
