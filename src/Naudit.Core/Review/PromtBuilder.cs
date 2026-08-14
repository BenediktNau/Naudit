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
        "Respond ONLY with a JSON object with exactly two fields: " +
        "\"summary\" - GitHub-flavored Markdown: a one-line overview (if there are no significant issues, say so briefly); " +
        "\"comments\" - an array of findings tied to a line, each " +
        "{ \"file\": <path exactly as shown>, \"line\": <new-file line number shown in the diff>, \"comment\": <Markdown>, " +
        "\"severity\": one of \"critical\", \"high\", \"medium\", \"low\", \"info\", " +
        "\"confidence\": one of \"high\", \"medium\", \"low\" }. " +
        "Set severity by the issue's objective impact: \"critical\" or \"high\" for correctness or security bugs, \"medium\" for real but non-urgent problems, and \"low\" or \"info\" for style or minor maintainability nitpicks. " +
        "Naudit derives the merge decision from these ratings via a configurable gate, so rate impact honestly and do NOT tune severity to any blocking threshold. " +
        "Set confidence by how certain you are the issue is real; prefer \"low\" when you cannot verify a claim from the diff alone. " +
        "Do NOT output an overall verdict - the merge decision is derived automatically from the findings' severity and confidence. " +
        "Only use a line number that is shown at the start of a line in the diff. " +
        "If a finding does not map to one specific changed line, still include it in \"comments\" with its \"severity\" and \"confidence\" but omit \"line\" (or set it to null) - do NOT invent a line number. " +
        "Use \"summary\" only for the one-line overview, never to carry findings. " +
        "A read-only \"Repository context\" section may follow the diff (surrounding code, usages, repository overview) - " +
        "use it to understand what the change does and how it fits, but report findings ONLY on the diff lines shown with line numbers. " +
        "A read-only \"Project memory\" section may follow - it contains maintainer decisions: " +
        "do NOT report findings matching a known false positive again, and treat the listed conventions " +
        "as authoritative project rules, not as issues to flag. " +
        "A read-only \"Project guidelines\" section may follow - it contains the project's own architecture and convention rules, " +
        "distilled from its documentation and curated by maintainers: treat them as authoritative and report violations of them as findings. " +
        "Also review the change at the architecture level: breaks of contracts or patterns the codebase itself establishes, and layering violations. " +
        "Such findings often map to no single changed line - report them without a line (omit \"line\") rather than dropping them. " +
        "For security, specifically check: new endpoints or handlers for missing authentication or authorization; " +
        "injection surfaces (SQL, command, path, SSRF); secrets or tokens in code or logs; and unsafe deserialization.";

    public static IList<ChatMessage> Build(
        string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes,
        IReadOnlyList<ScanFinding>? findings = null, ReviewContext? context = null,
        IReadOnlyList<MemoryEntry>? memory = null, bool toolsAvailable = false, string? guidelines = null)
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

        AppendContext(sb, context);
        AppendFindings(sb, findings ?? []);
        AppendToolGuidance(sb, toolsAvailable);
        AppendGuidelines(sb, guidelines);
        AppendMemory(sb, memory);

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

    // Repo-Kontext als read-only Grounding: umgebender Code, Call-Sites, Überblick. Leerer Kontext
    // rendert nichts (Prompt bleibt byte-identisch zum diff-only-Pfad).
    private static void AppendContext(StringBuilder sb, ReviewContext? context)
    {
        if (context is null)
            return;
        var hasAny = context.Environments.Count > 0 || context.Usages.Count > 0 || context.Overview is not null;
        if (!hasAny)
            return;

        sb.AppendLine();
        sb.AppendLine("# Repository context (read-only grounding from the checked-out repo)");
        sb.AppendLine("Use it to understand the change; report findings ONLY on the diff lines above.");

        if (context.Environments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Surrounding code");
            foreach (var e in context.Environments)
            {
                sb.AppendLine();
                var label = e.IsFullFile ? "full file" : $"from line {e.StartLine}";
                sb.AppendLine($"### {e.FilePath} ({label})");
                AppendFenced(sb, e.Content);
            }
        }

        if (context.Usages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Usages of changed symbols");
            foreach (var u in context.Usages)
            {
                sb.AppendLine();
                sb.AppendLine($"### `{u.Symbol}` — {u.FilePath}:{u.Line}");
                AppendFenced(sb, u.Snippet);
            }
        }

        if (context.Overview is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Repository overview");
            sb.AppendLine(context.Overview);
        }
    }

    // Umschließt Inhalt in einem Codeblock, dessen Fence LÄNGER ist als der längste Backtick-Lauf
    // im Inhalt (CommonMark). Sonst würde Datei-/Snippet-Inhalt mit ``` den Block vorzeitig schließen
    // und der Rest liefe unstrukturiert in den Prompt.
    private static void AppendFenced(StringBuilder sb, string content)
    {
        int longest = 0, run = 0;
        foreach (var ch in content)
        {
            if (ch == '`') { run++; if (run > longest) longest = run; }
            else run = 0;
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        sb.AppendLine(fence);
        sb.AppendLine(content);
        sb.AppendLine(fence);
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

        // Secrets zuerst (am dringlichsten), dann Dependencies, dann SAST.
        AppendCategory(sb, "Secrets", findings.Where(f => f.Category == FindingCategory.Secrets));
        AppendCategory(sb, "Dependency / SCA", findings.Where(f => f.Category == FindingCategory.Sca));
        AppendCategory(sb, "SAST", findings.Where(f => f.Category == FindingCategory.Sast));
    }

    // Nur wenn dem Review Tools angeboten werden: knapper Hinweis, WANN das Docs-Werkzeug sinnvoll ist.
    // Ohne Hinweis ruft das Modell das Tool nie oder ständig — beides verbrennt Budget.
    private static void AppendToolGuidance(StringBuilder sb, bool toolsAvailable)
    {
        if (!toolsAvailable)
            return;
        sb.AppendLine();
        sb.AppendLine("# Tools available");
        sb.AppendLine("You can call a tool to fetch current documentation for a library (Context7). " +
            "Use it when the diff uses an API you are unsure about, rather than guessing against possibly-outdated knowledge. " +
            "Do not use it for well-known stdlib or trivial code. After any tool use, still respond with the required review JSON.");
    }

    // Architektur-Profil: destillierte, maintainer-kuratierte Projekt-Guidelines — autoritativ,
    // direkt vor dem Memory (beide tragen Maintainer-Entscheidungen; Memory bleibt zuletzt).
    private static void AppendGuidelines(StringBuilder sb, string? guidelines)
    {
        if (string.IsNullOrWhiteSpace(guidelines))
            return;
        sb.AppendLine();
        sb.AppendLine("# Project guidelines (distilled from this repository's own documentation; maintainer-curated, authoritative)");
        sb.AppendLine();
        sb.AppendLine(guidelines.Trim());
    }

    // Maintainer-Guidance als LETZTE Sektion (am nächsten an der Antwort = höchstes Gewicht).
    // Leeres Gedächtnis rendert nichts — der Prompt bleibt byte-identisch zu heute.
    private static void AppendMemory(StringBuilder sb, IReadOnlyList<MemoryEntry>? memory)
    {
        if (memory is null || memory.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("# Project memory (maintainer guidance)");

        AppendMemoryGroup(sb, "## Known false positives — do NOT report these or equivalent findings again",
            memory.Where(m => m.Kind == MemoryKind.FalsePositive));
        AppendMemoryGroup(sb, "## Project conventions — respect these when judging the diff",
            memory.Where(m => m.Kind == MemoryKind.Convention));
    }

    private static void AppendMemoryGroup(StringBuilder sb, string heading, IEnumerable<MemoryEntry> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0)
            return;
        sb.AppendLine();
        sb.AppendLine(heading);
        foreach (var m in list)
        {
            var scope = string.IsNullOrEmpty(m.File) ? "" : $"{m.File}: ";
            var note = string.IsNullOrEmpty(m.Reason) ? "" : $" (maintainer note: {m.Reason})";
            sb.AppendLine($"- {scope}{m.Text}{note}");
        }
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
