using System.Text;
using Microsoft.Extensions.AI;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

public static class PromptBuilder
{
    public const string DefaultSystemPrompt =
        "You are Naudit, a senior code reviewer. Review the merge request diff below. " +
        "Focus on correctness bugs, security issues and clear maintainability problems. Be concise. " +
        "Respond ONLY with a JSON object with exactly two fields: " +
        "\"summary\" - GitHub-flavored Markdown (a one-line summary followed by a bullet list of findings; " +
        "if there are no significant issues, say so briefly) - and " +
        "\"verdict\" - either \"approve\" or \"request_changes\" " +
        "(use \"request_changes\" only when there are correctness or security bugs that should block the merge).";

    public static IList<ChatMessage> Build(string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Merge Request: {request.Title}");
        foreach (var change in changes)
        {
            sb.AppendLine();
            sb.AppendLine($"## File: {change.FilePath}");
            sb.AppendLine("```diff");
            sb.AppendLine(change.Diff);
            sb.AppendLine("```");
        }

        return new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, sb.ToString()),
        };
    }
}
