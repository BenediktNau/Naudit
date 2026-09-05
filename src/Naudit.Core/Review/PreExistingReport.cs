using System.Text;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

/// <summary>Rendert die Altlasten-Übersicht als eigenständigen MR/PR-Kommentar (deutsch, wie die
/// Summary). Bewusst getrennt von der englischen Prompt-Sektion: gleiche Verdichtung, zwei
/// Zielgruppen. Der Bericht nennt nie ein Verdikt — Altlasten beeinflussen die Merge-Entscheidung
/// nicht.</summary>
public static class PreExistingReport
{
    public static string Markdown(PreExistingSummary s)
    {
        var sb = new StringBuilder();
        var counts = string.Join(", ", s.BySeverity.Select(x => $"{x.Count} {x.Severity}"));
        sb.AppendLine($"### 🗂️ Vorbestehende Werkzeugbefunde ({s.Total})");
        sb.AppendLine();
        sb.AppendLine($"Diese Funde stammen aus dem Repository-Scan und sind **nicht durch diesen Merge Request verursacht** — sie beeinflussen die Merge-Entscheidung nicht. Verteilung: {counts}.");

        if (s.Detailed.Count > 0)
        {
            sb.AppendLine();
            var head = s.DetailedOmitted > 0
                ? $"**Höchste Schweregrade** (erste {s.Detailed.Count} von {s.Detailed.Count + s.DetailedOmitted}):"
                : $"**Höchste Schweregrade** ({s.Detailed.Count}):";
            sb.AppendLine(head);
            sb.AppendLine();
            foreach (var f in s.Detailed)
            {
                var loc = f.FilePath is null ? "" : f.Line is int ln ? $"`{f.FilePath}:{ln}` " : $"`{f.FilePath}` ";
                var rule = f.RuleId is null ? f.Tool : f.RuleId;
                sb.AppendLine($"- {loc}{rule} ({f.Severity})");
            }
        }

        if (s.Groups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<details>");
            var rest = s.GroupsOmitted > 0
                ? $"Übrige {s.GroupedTotal} Befunde nach Regel (Top {s.Groups.Count}, {s.GroupsOmitted} weitere Regeln nicht gezeigt)"
                : $"Übrige {s.GroupedTotal} Befunde nach Regel ({s.Groups.Count})";
            sb.AppendLine($"<summary>{rest}</summary>");
            sb.AppendLine();
            sb.AppendLine("| Regel | Schweregrad | Anzahl | Beispiel |");
            sb.AppendLine("| --- | --- | ---: | --- |");
            foreach (var g in s.Groups)
            {
                var example = g.ExampleFilePath is null ? "—"
                    : g.ExampleLine is int ln ? $"`{g.ExampleFilePath}:{ln}`" : $"`{g.ExampleFilePath}`";
                sb.AppendLine($"| {g.Rule} | {g.Severity} | {g.Count} | {example} |");
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
        }

        return sb.ToString().TrimEnd();
    }
}
