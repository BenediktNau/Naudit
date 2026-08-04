using Naudit.Benchmark;

namespace Naudit.Tests;

public class BenchmarkDatasetTests
{
    [Theory]
    // Upstream-PR (35 der 50 Einträge)
    [InlineData("https://github.com/getsentry/sentry/pull/93824", "getsentry/sentry", 93824)]
    [InlineData("https://github.com/calcom/cal.com/pull/21437", "calcom/cal.com", 21437)]
    [InlineData("https://github.com/grafana/grafana/pull/105892", "grafana/grafana", 105892)]
    // Vorbereitungs-Org (15 der 50 Einträge) — Punkt/Bindestrich im Repo-Namen, kleine Nummern
    [InlineData("https://github.com/ai-code-review-evaluation/discourse-graphite/pull/1",
        "ai-code-review-evaluation/discourse-graphite", 1)]
    [InlineData("https://github.com/ai-code-review-evaluation/sentry-greptile/pull/5",
        "ai-code-review-evaluation/sentry-greptile", 5)]
    public void Parse_liest_Projekt_und_Nummer_aus_beiden_URL_Formen(string url, string projectId, int number)
    {
        var entry = GoldenDataset.Parse(url, "Titel");

        Assert.Equal(projectId, entry.ProjectId);
        Assert.Equal(number, entry.Number);
        Assert.Equal(url, entry.Url);
        Assert.Equal("Titel", entry.PrTitle);
    }

    [Theory]
    [InlineData("https://github.com/discourse/discourse/commit/ffbaf8c5")]   // Commit, kein PR
    [InlineData("https://github.com/getsentry/sentry/pull/")]                 // Nummer fehlt
    [InlineData("https://example.com/getsentry/sentry/pull/1")]               // fremder Host
    [InlineData("")]
    public void Parse_wirft_bei_unbrauchbarer_URL(string url)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GoldenDataset.Parse(url, "Titel"));
        Assert.Contains(url, ex.Message);
    }

    [Fact]
    public void Load_liest_alle_Eintraege_aus_allen_JSON_Dateien()
    {
        var dir = Directory.CreateTempSubdirectory("naudit-golden-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "sentry.json"), """
            [
              {"pr_title": "Erster",  "url": "https://github.com/getsentry/sentry/pull/1", "comments": []},
              {"pr_title": "Zweiter", "url": "https://github.com/getsentry/sentry/pull/2", "comments": []}
            ]
            """);
            File.WriteAllText(Path.Combine(dir.FullName, "discourse.json"), """
            [
              {"pr_title": "Dritter",
               "url": "https://github.com/ai-code-review-evaluation/discourse-graphite/pull/3",
               "comments": []}
            ]
            """);

            var entries = GoldenDataset.Load(dir.FullName);

            Assert.Equal(3, entries.Count);
            Assert.Contains(entries, e => e.ProjectId == "ai-code-review-evaluation/discourse-graphite" && e.Number == 3);
            Assert.Contains(entries, e => e.PrTitle == "Erster");
        }
        finally { dir.Delete(recursive: true); }
    }
}
