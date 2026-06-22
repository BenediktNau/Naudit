using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

public sealed class ReviewService(IChatClient chatClient, IGitPlatform gitPlatform, ReviewOptions options)
{
    // Web-Defaults: camelCase + case-insensitive — passt zu summary/verdict/comments.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var changes = await gitPlatform.GetChangesAsync(request, ct);
        if (changes.Count == 0)
            return new ReviewResult(string.Empty, ReviewVerdict.Approve);

        var messages = PromptBuilder.Build(options.SystemPrompt, request, changes);

        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        var response = await chatClient.GetResponseAsync(messages, chatOptions, ct);

        var parsed = JsonSerializer.Deserialize<LlmReviewResponse>(response.Text, JsonOpts)
            ?? throw new InvalidOperationException("LLM lieferte keine parsebare Review-Antwort.");

        // Fail-closed: nur explizite Verdicts; alles andere ist ein Fehler.
        var verdict = parsed.Verdict?.ToLowerInvariant() switch
        {
            "request_changes" => ReviewVerdict.RequestChanges,
            "approve" => ReviewVerdict.Approve,
            _ => throw new InvalidOperationException($"Unerwartetes Verdict vom LLM: '{parsed.Verdict}'."),
        };

        // Jede Finding gegen die kommentierbaren Diff-Zeilen prüfen.
        var commentable = DiffParser.Parse(changes);
        var inline = new List<InlineComment>();
        var orphans = new List<LlmComment>();
        foreach (var c in parsed.Comments ?? [])
        {
            if (!string.IsNullOrEmpty(c.File) && commentable.TryGetValue(c.File, out var lines) && lines.TryGetValue(c.Line, out var oldLine))
                inline.Add(new InlineComment(c.File, c.Line, oldLine, c.Comment));
            else
                orphans.Add(c);
        }

        var summary = ComposeSummary(parsed.Summary, verdict, inline.Count, orphans);
        await gitPlatform.PostReviewAsync(request, summary, inline, ct);
        return new ReviewResult(summary, verdict);
    }

    // Schlanker Hybrid: LLM-Überblick + Verdict-Zeile + Count + nicht-verortbare Findings.
    private static string ComposeSummary(string? llmSummary, ReviewVerdict verdict, int inlineCount, IReadOnlyList<LlmComment> orphans)
    {
        var sb = new StringBuilder();
        sb.AppendLine((llmSummary ?? string.Empty).TrimEnd());
        sb.AppendLine();
        var verdictText = verdict == ReviewVerdict.RequestChanges ? "⚠️ request_changes" : "✅ approve";
        sb.AppendLine($"**Verdict:** {verdictText} · {inlineCount} inline, {orphans.Count} ohne Position");
        if (orphans.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Findings ohne Position:**");
            foreach (var o in orphans)
            {
                var where = string.IsNullOrEmpty(o.File) ? "" : $"`{o.File}` ";
                sb.AppendLine($"- {where}{o.Comment}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    // Wire-DTO für die LLM-Antwort. Verdict bewusst als string (Mapping oben).
    private sealed record LlmReviewResponse(string? Summary, string Verdict, List<LlmComment>? Comments);

    private sealed record LlmComment(string? File, int Line, string Comment);
}
