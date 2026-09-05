using Naudit.Core.Models;
using Naudit.Core.Review;
using Xunit;

namespace Naudit.Tests;

public class PreExistingReportTests
{
    [Fact]
    public void Markdown_namesHighFindings_andCountsTheRest()
    {
        var summary = PreExistingSummary.Build(
            [
                new ScanFinding("opengrep", FindingCategory.Sast, FindingSeverity.High, "msg",
                    "detected-generic-api-key", ".env.example", 72),
                .. Enumerable.Range(0, 1763).Select(i => new ScanFinding("opengrep", FindingCategory.Sast,
                    FindingSeverity.Medium, "msg", "i18next-key-format", $"f{i}.tsx", i)),
            ],
            new PreExistingOptions());

        var md = PreExistingReport.Markdown(summary);

        Assert.Contains("Vorbestehende Werkzeugbefunde", md);
        Assert.Contains("1764", md);                          // Gesamtzahl
        Assert.Contains(".env.example:72", md);               // High einzeln, verortet
        Assert.Contains("detected-generic-api-key", md);
        Assert.Contains("i18next-key-format", md);
        Assert.Contains("1763", md);                          // Rest als Zaehler
        Assert.Contains("nicht durch diesen", md);            // Abgrenzung zum MR
        Assert.DoesNotContain("Verdict", md);                 // beeinflusst die Merge-Entscheidung nie
    }

    [Fact]
    public void Markdown_neverIncludes_findingMessage()
    {
        // Sicherheitsgarantie (siehe Warnkommentar in PreExistingReport.Markdown): s.Detailed
        // traegt UNREDIGIERTE ScanFinding-Objekte (Redaction laeuft nur ueber reduction.Selected).
        // Bei einem Secrets-Detektor steht der Wert selbst in ScanFinding.Message — die darf hier
        // nie ausgegeben werden, sonst landet ein Secret unredigiert in einem dauerhaften,
        // bei oeffentlichen Repos weltlesbaren PR-Kommentar.
        const string secretMarker = "sk-live-ZZTOPSECRETMARKER-1234567890";
        var summary = PreExistingSummary.Build(
            [
                new ScanFinding("betterleaks", FindingCategory.Sast, FindingSeverity.High, secretMarker,
                    "detected-generic-api-key", "config.yaml", 5),
            ],
            new PreExistingOptions());

        var md = PreExistingReport.Markdown(summary);

        Assert.Contains("config.yaml:5", md);                 // Fund wird trotzdem verortet
        Assert.DoesNotContain(secretMarker, md);
    }
}
