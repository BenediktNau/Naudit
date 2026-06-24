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
        "Static-analysis and dependency-scan results may be provided below as grounding; treat them as reliable signals. " +
        "Assume the project's target framework and toolchain are valid and current; do NOT flag a framework or SDK version as nonexistent or unsupported. " +
        "Respond ONLY with a JSON object with exactly three fields: " +
        "\"verdict\" - either \"approve\" or \"request_changes\" " +
        "(use \"request_changes\" only when there are correctness or security bugs that should block the merge); " +
        "\"summary\" - GitHub-flavored Markdown: a one-line overview (if there are no significant issues, say so briefly); " +
        "\"comments\" - an array of findings tied to a line, each " +
        "{ \"file\": <path exactly as shown>, \"line\": <new-file line number shown in the diff>, \"comment\": <Markdown> }. " +
        "Only use a line number that is shown at the start of a line in the diff. " +
        "If a finding does not map to one specific changed line, omit it from \"comments\" and mention it in \"summary\" instead.";

    public static IList<ChatMessage> Build(
        string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes,
        IReadOnlyList<ScanFinding>? findings = null)
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

        AppendFindings(sb, findings ?? []);

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
        var inHunk = false;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                newLine = DiffParser.ParseHunkHeader(line).NewStart - 1;
                inHunk = true;
                sb.AppendLine($"     {line}");
                continue;
            }
            // Datei-Header (+++/---) stehen nur vor dem ersten Hunk; innerhalb eines Hunks
            // sind +/- echte Inhaltszeilen und müssen ihre New-File-Nummer behalten.
            if (!inHunk && (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)))
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

    // Grounding-Sektion: alle Funde repo-weit, annotiert [in diff]/[pre-existing], gruppiert nach Category.
    private static void AppendFindings(StringBuilder sb, IReadOnlyList<ScanFinding> findings)
    {
        sb.AppendLine();
        sb.AppendLine("# Static-analysis & dependency findings (grounding — tools run on the repo, treat as reliable)");
        if (findings.Count == 0)
        {
            sb.AppendLine("No tool findings.");
            return;
        }
        sb.AppendLine("Prioritize [in diff] (introduced/touched by this MR). [pre-existing] were already in the repo.");

        AppendCategory(sb, "Dependency / SCA", findings.Where(f => f.Category == FindingCategory.Sca));
        AppendCategory(sb, "SAST", findings.Where(f => f.Category == FindingCategory.Sast));
    }

    private static void AppendCategory(StringBuilder sb, string heading, IEnumerable<ScanFinding> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
            return;
        sb.AppendLine();
        sb.AppendLine($"## {heading}");
        foreach (var f in list)
        {
            var scope = f.InDiff ? "in diff" : "pre-existing";
            var loc = f.FilePath is null ? "" : f.Line is int ln ? $" · {f.FilePath}:{ln}" : $" · {f.FilePath}";
            var rule = f.RuleId is null ? "" : $" · {f.RuleId}";
            sb.AppendLine($"- [{f.Severity.ToString().ToUpperInvariant()}][{scope}] {f.Tool}{rule}{loc} → {f.Message}");
        }
    }
}
