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

        var verdict = string.Equals(parsed.Verdict, "request_changes", StringComparison.OrdinalIgnoreCase)
            ? ReviewVerdict.RequestChanges
            : ReviewVerdict.Approve;

        await gitPlatform.PostSummaryAsync(request, parsed.Summary, ct);
        return new ReviewResult(parsed.Summary, verdict);
    }

    // Wire-DTO für die LLM-Antwort. Verdict bewusst als string (kein Enum),
    // um Enum-JSON-Fragilität zu vermeiden; Mapping erfolgt oben.
    private sealed record LlmReviewResponse(string Summary, string Verdict);
}
