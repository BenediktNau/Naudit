using System.Text;
using Microsoft.Extensions.AI;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

public static class PromptBuilder
{
    public const string DefaultSystemPrompt =
        "You are Naudit, a senior code reviewer. Review the merge request diff below. " +
        "Focus on correctness bugs, security issues and clear maintainability problems. " +
        "Be concise. Answer in GitHub-flavored Markdown: a one-line summary followed by a bullet list of findings. " +
        "If there are no significant issues, say so briefly.";

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
