using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

public sealed class ReviewService(
    IChatClient chatClient,
    IGitPlatform gitPlatform,
    ReviewOptions options,
    IWorkspaceProvider workspaceProvider,
    IEnumerable<ISastAnalyzer> analyzers,
    IFindingReducer findingReducer)
{
    // Web-Defaults: camelCase + case-insensitive — passt zu summary/verdict/comments.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyList<ISastAnalyzer> _analyzers = analyzers.ToList();

    public async Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var changes = await gitPlatform.GetChangesAsync(request, ct);
        if (changes.Count == 0)
            return new ReviewResult(string.Empty, ReviewVerdict.Approve);

        // SAST/SCA-Grounding vor dem Prompt-Aufbau einsammeln (leer, wenn Feature aus).
        var findings = await CollectFindingsAsync(request, changes, ct);

        var messages = PromptBuilder.Build(options.SystemPrompt, request, changes, findings);

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
            // Leerer/fehlender Body würde beim Plattform-POST scheitern -> solche Findings verwerfen.
            var body = c.Comment?.Trim();
            if (string.IsNullOrEmpty(body))
                continue;

            if (!string.IsNullOrEmpty(c.File) && commentable.TryGetValue(c.File, out var lines) && lines.TryGetValue(c.Line, out var oldLine))
                inline.Add(new InlineComment(c.File, c.Line, oldLine, body));
            else
                orphans.Add(c with { Comment = body });
        }

        var summary = ComposeSummary(parsed.Summary, verdict, inline.Count, orphans);
        await gitPlatform.PostReviewAsync(request, summary, inline, ct);
        return new ReviewResult(summary, verdict);
    }

    // SAST/SCA-Grounding. Ohne Analyzer (Feature aus) sofort leer → exakt diff-only wie früher.
    // Checkout-Fehler degradiert auf diff-only; ein einzelner Analyzer-Fehler kippt den Review nicht.
    private async Task<IReadOnlyList<ScanFinding>> CollectFindingsAsync(
        ReviewRequest request, IReadOnlyList<CodeChange> changes, CancellationToken ct)
    {
        if (_analyzers.Count == 0)
            return [];

        IReviewWorkspace workspace;
        try
        {
            workspace = await workspaceProvider.CheckoutAsync(request, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return []; // Checkout fehlgeschlagen → diff-only (Infrastructure hat geloggt)
        }

        await using (workspace)
        {
            var results = await Task.WhenAll(_analyzers.Select(a => SafeAnalyzeAsync(a, workspace, changes, ct)));

            var changed = new HashSet<string>(changes.Select(c => c.FilePath));
            var annotated = results
                .SelectMany(r => r)
                .Select(f => f.FilePath is not null && changed.Contains(f.FilePath) ? f with { InDiff = true } : f)
                .ToList();

            return await findingReducer.ReduceAsync(annotated, changes, ct);
        }
    }

    private static async Task<IReadOnlyList<ScanFinding>> SafeAnalyzeAsync(
        ISastAnalyzer analyzer, IReviewWorkspace ws, IReadOnlyList<CodeChange> changes, CancellationToken ct)
    {
        try { return await analyzer.AnalyzeAsync(ws, changes, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return []; }
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

    private sealed record LlmComment(string? File, int Line, string? Comment);
}
