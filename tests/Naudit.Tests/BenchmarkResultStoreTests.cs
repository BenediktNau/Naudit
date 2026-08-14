using Naudit.Benchmark;

namespace Naudit.Tests;

public class BenchmarkResultStoreTests
{
    private static BenchmarkRecord Record(string url, int number, string? error = null, int commentCount = 1) => new(
        url,
        new CapturedReview("getsentry/sentry", number, "Zusammenfassung", "Approve",
            Enumerable.Range(0, commentCount)
                .Select(i => new CapturedComment("a.cs", 5 + i, $"Fund{i}", "High", "Medium"))
                .ToList()),
        new ReviewDiagnostics(CheckoutRequested: true, CheckoutFailed: false, HeadRef: "refs/pull/1/head", HeadSha: "0123456789abcdef0123456789abcdef01234567",
            ContextInPrompt: true, GuidelinesInPrompt: true, InputTokens: 1000, OutputTokens: 200,
            ChangedFiles: 7, Warnings: [], DurationSeconds: 12.5, Error: error));

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
    public void CleanUrls_laesst_auffaellige_Reviews_aus_damit_sie_wiederholt_werden()
    {
        var dir = Directory.CreateTempSubdirectory("naudit-store-");
        try
        {
            var path = Path.Combine(dir.FullName, "naudit-reviews.json");
            var store = new ResultStore(path);
            store.Append(Record("https://github.com/getsentry/sentry/pull/1", 1));
            store.Append(Record("https://github.com/getsentry/sentry/pull/2", 2, error: "Zeitüberschreitung"));

            // Geschrieben sind beide — der Wiederaufsetzpunkt kennt aber nur das saubere Review.
            Assert.Equal(2, store.CompletedUrls.Count);
            Assert.Single(store.CleanUrls);
            Assert.Contains("https://github.com/getsentry/sentry/pull/1", store.CleanUrls);
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
    public void Append_ersetzt_einen_vorhandenen_Eintrag_derselben_URL_mit_neuen_Daten()
    {
        var dir = Directory.CreateTempSubdirectory("naudit-store-");
        try
        {
            var path = Path.Combine(dir.FullName, "naudit-reviews.json");
            var store = new ResultStore(path);

            // Erste Versuch: fehlgeschlagenes Review mit Error.
            store.Append(Record("https://github.com/getsentry/sentry/pull/1", 1, error: "Checkout failed", commentCount: 0));
            var all = store.All();
            Assert.Single(all);
            Assert.Equal("Checkout failed", all[0].Diagnostics.Error);
            Assert.Empty(all[0].Review.Comments);

            // Zweite Versuch: erfolgreiches Review (Error: null), andere Kommentaranzahl.
            store.Append(Record("https://github.com/getsentry/sentry/pull/1", 1, error: null, commentCount: 3));

            // Immer noch nur ein Eintrag (ersetzt, nicht hinzugefügt).
            all = store.All();
            Assert.Single(all);
            // Werte stammen aus dem zweiten Versuch.
            Assert.Null(all[0].Diagnostics.Error);
            Assert.Equal(3, all[0].Review.Comments.Count);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Append_behält_die_Reihenfolge_beibei_Ersetzen()
    {
        var dir = Directory.CreateTempSubdirectory("naudit-store-");
        try
        {
            var path = Path.Combine(dir.FullName, "naudit-reviews.json");
            var store = new ResultStore(path);

            // Drei URLs anhängen.
            store.Append(Record("https://github.com/getsentry/sentry/pull/1", 1));
            store.Append(Record("https://github.com/getsentry/sentry/pull/2", 2));
            store.Append(Record("https://github.com/getsentry/sentry/pull/3", 3));

            // Erste erneut anhängen (mit neuen Daten).
            store.Append(Record("https://github.com/getsentry/sentry/pull/1", 1, error: "Rerun"));

            // Reihenfolge sollte 1, 2, 3 bleiben (nicht 2, 3, 1).
            var all = store.All();
            Assert.Equal(3, all.Count);
            Assert.Equal("https://github.com/getsentry/sentry/pull/1", all[0].Url);
            Assert.Equal("https://github.com/getsentry/sentry/pull/2", all[1].Url);
            Assert.Equal("https://github.com/getsentry/sentry/pull/3", all[2].Url);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void ResultStore_überlebt_abgeschnittene_JSON_und_startet_leer()
    {
        var dir = Directory.CreateTempSubdirectory("naudit-store-");
        try
        {
            var path = Path.Combine(dir.FullName, "naudit-reviews.json");

            // Absichtlich abgeschnittene JSON-Datei schreiben.
            File.WriteAllText(path, "[{\"url\":\"https://github.com/test/test/pull/1\",\"review\":{\"project\":");

            // ResultStore sollte das überleben, eine Diagnose ausgeben, und mit leerer Liste weitermachen.
            var store = new ResultStore(path);

            // Sollte leer sein.
            Assert.Empty(store.CompletedUrls);
            Assert.Empty(store.All());

            // Korrupte Datei sollte verschoben worden sein.
            Assert.True(File.Exists($"{path}.corrupt"), "Korrupte Datei sollte nach .corrupt verschoben worden sein");
            Assert.False(File.Exists(path), "Ursprüngliche Datei sollte nicht mehr existieren");

            // Neuer Eintrag sollte sich speichern lassen (frischer Start).
            store.Append(Record("https://github.com/getsentry/sentry/pull/1", 1));
            Assert.Single(store.CompletedUrls);
        }
        finally { dir.Delete(recursive: true); }
    }
}
