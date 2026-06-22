using System.Text;
using Microsoft.Extensions.AI;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

public static class PromptBuilder
{
    public const string DefaultSystemPrompt =
        "You are Naudit, a senior code reviewer. Review the merge request diff below. " +
        "Each diff line is prefixed with its line number in the NEW file (blank for removed lines). " +
        "Focus on correctness bugs, security issues and clear maintainability problems. Be concise. " +
        "Respond ONLY with a JSON object with exactly three fields: " +
        "\"verdict\" - either \"approve\" or \"request_changes\" " +
        "(use \"request_changes\" only when there are correctness or security bugs that should block the merge); " +
        "\"summary\" - GitHub-flavored Markdown: a one-line overview (if there are no significant issues, say so briefly); " +
        "\"comments\" - an array of findings tied to a line, each " +
        "{ \"file\": <path exactly as shown>, \"line\": <new-file line number shown in the diff>, \"comment\": <Markdown> }. " +
        "Only use a line number that is shown at the start of a line in the diff. " +
        "If a finding does not map to one specific changed line, omit it from \"comments\" and mention it in \"summary\" instead.";

    public static IList<ChatMessage> Build(string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Merge Request: {request.Title}");
        foreach (var change in changes)
        {
            sb.AppendLine();
            sb.AppendLine($"## File: {change.FilePath}");
            sb.AppendLine("```diff");
            AppendAnnotatedDiff(sb, change.Diff);
            sb.AppendLine("```");
        }

        return new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, sb.ToString()),
        };
    }

    // Stellt jeder Diff-Zeile ihre New-File-Zeilennummer voran (leer bei gelöschten/Header-Zeilen),
    // damit das LLM eine stabile, reale Zeilennummer referenzieren kann.
    private static void AppendAnnotatedDiff(StringBuilder sb, string diff)
    {
        int newLine = 0;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                newLine = DiffParser.ParseHunkHeader(line).NewStart - 1;
                sb.AppendLine($"     {line}");
                continue;
            }
            if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
            {
                sb.AppendLine($"     {line}");
                continue;
            }
            if (line.Length > 0 && (line[0] == '+' || line[0] == ' '))
            {
                newLine++;
                sb.AppendLine($"{newLine,4} {line}");
            }
            else
            {
                // gelöschte Zeile / sonstiges: keine New-File-Nummer
                sb.AppendLine($"     {line}");
            }
        }
    }
}
