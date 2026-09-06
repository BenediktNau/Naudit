using System.Text;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

/// <summary>Rendert die Altlasten-Übersicht als eigenständigen MR/PR-Kommentar (deutsch, wie die
/// Summary). Bewusst getrennt von der englischen Prompt-Sektion: gleiche Verdichtung, zwei
/// Zielgruppen. Der Bericht nennt nie ein Verdikt — Altlasten beeinflussen die Merge-Entscheidung
/// nicht.</summary>
public static class PreExistingReport
{
    // Redaction: absichtlich WIRD HIER NUR Regel-Id, Pfad, Zeile, Tool und Zaehler gerendert,
    // NIEMALS ScanFinding.Message — deshalb braucht dieser Kommentar keinen zusaetzlichen
    // Redactor-Durchlauf. Das ist HIER sogar noch schaerfer als bei PromtBuilder.AppendBaseline:
    // s.Detailed traegt UNREDIGIERTE ScanFinding-Objekte (die Redaction in ReviewService laeuft
    // nur ueber reduction.Selected, nicht ueber reduction.PreExisting), und dieser Text landet als
    // eigenstaendiger, dauerhafter Kommentar direkt im MR/PR — bei einem oeffentlichen Repo
    // weltlesbar. Bei einem Secrets-Detektor steht der Wert selbst in der Nachricht. Die
    // Fund-Nachricht hier NICHT ergaenzen, ohne diese Garantie neu zu pruefen.
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

            // Secrets-Kategorie: NIE einzeln mit Fundort — nur als Regel + Anzahl. Dieselbe
            // Sonderbehandlung gilt in PromtBuilder.AppendBaseline (das Modell koennte den Fundort
            // sonst ueber Summary/Kommentar oeffentlich wiedergeben). Grund: BetterleaksAnalyzer
            // stuft JEDEN Secrets-Fund pauschal auf High ein, landet also komplett in dieser
            // Einzelliste. Ohne diese Sonderbehandlung waeren das bis zu MaxDetailed Zeilen der
            // Form "`pfad:zeile` detected-generic-api-key (High)" — eine durchsuchbare Landkarte
            // aller (mutmasslichen) Secret-Fundorte in einem dauerhaften, bei oeffentlichen Repos
            // weltlesbaren PR-Kommentar. Der Wert selbst steht dank der Nachricht-Garantie oben
            // ohnehin nie drin — die Fundort-Liste allein waere aber schon eine Verstaerkung.
            foreach (var g in s.Detailed
                         .Where(f => f.Category == FindingCategory.Secrets)
                         .GroupBy(f => f.RuleId ?? f.Tool)
                         .Select(g => (Rule: g.Key, Severity: g.Max(f => f.Severity), Count: g.Count()))
                         .OrderByDescending(g => g.Severity).ThenByDescending(g => g.Count).ThenBy(g => g.Rule))
            {
                sb.AppendLine($"- {g.Rule} ({g.Severity}) — {g.Count}× (Fundort nicht angezeigt, Secrets)");
            }

            foreach (var f in s.Detailed.Where(f => f.Category != FindingCategory.Secrets))
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
                // Derselbe Sonderfall wie oben bei s.Detailed, hier NUR fuer den Beispielort: eine
                // Secrets-Gruppe entsteht entweder ueber eine Severity unterhalb von DetailSeverity
                // oder ueber einen Ueberlauf aus der Einzelliste (mehr als MaxDetailed Secrets-Funde)
                // — beide Wege umgehen die Sonderbehandlung dort, wenn hier nicht ebenfalls maskiert
                // wird. Regel, Severity und Anzahl bleiben (die sagen nichts ueber einen Fundort).
                var example = g.Category == FindingCategory.Secrets ? "—"
                    : g.ExampleFilePath is null ? "—"
                    : g.ExampleLine is int ln ? $"`{g.ExampleFilePath}:{ln}`" : $"`{g.ExampleFilePath}`";
                sb.AppendLine($"| {g.Rule} | {g.Severity} | {g.Count} | {example} |");
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
        }

        return sb.ToString().TrimEnd();
    }
}
