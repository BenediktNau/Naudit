using Naudit.Benchmark;

namespace Naudit.Tests;

public class BenchmarkResultStoreTests
{
    private static BenchmarkRecord Record(string url, int number) => new(
        url,
        new CapturedReview("getsentry/sentry", number, "Zusammenfassung", "Approve",
            [new CapturedComment("a.cs", 5, "Fund", "High", "Medium")]),
        new ReviewDiagnostics(CheckoutRequested: true, Warnings: [], DurationSeconds: 12.5, Error: null));

    [Fact]
    public void CompletedUrls_ist_leer_wenn_die_Datei_noch_nicht_existiert()
    {
        var dir = Directory.CreateTempSubdirectory("naudit-store-");
        try
        {
            var store = new ResultStore(Path.Combine(dir.FullName, "naudit-reviews.json"));
            Assert.Empty(store.CompletedUrls);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Append_schreibt_sofort_und_ein_neuer_Store_liest_es_wieder()
    {
        var dir = Directory.CreateTempSubdirectory("naudit-store-");
        try
        {
            var path = Path.Combine(dir.FullName, "naudit-reviews.json");
            var first = new ResultStore(path);
            first.Append(Record("https://github.com/getsentry/sentry/pull/1", 1));
            first.Append(Record("https://github.com/getsentry/sentry/pull/2", 2));

            // Neuer Store = neuer Prozessstart nach Abbruch.
            var second = new ResultStore(path);

            Assert.Equal(2, second.CompletedUrls.Count);
            Assert.Contains("https://github.com/getsentry/sentry/pull/1", second.CompletedUrls);
            var all = second.All();
            Assert.Equal(2, all.Count);
            Assert.Equal("Zusammenfassung", all[0].Review.Summary);
            Assert.True(all[0].Diagnostics.CheckoutRequested);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Append_ersetzt_einen_vorhandenen_Eintrag_derselben_URL()
    {
        var dir = Directory.CreateTempSubdirectory("naudit-store-");
        try
        {
            var path = Path.Combine(dir.FullName, "naudit-reviews.json");
            var store = new ResultStore(path);
            store.Append(Record("https://github.com/getsentry/sentry/pull/1", 1));
            store.Append(Record("https://github.com/getsentry/sentry/pull/1", 1));

            Assert.Single(store.All());
        }
        finally { dir.Delete(recursive: true); }
    }
}
