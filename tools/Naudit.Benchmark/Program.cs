using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Naudit.Benchmark;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Data;

// Pflichtangaben: Klon des Benchmarks + Ausgabedatei. Optionale Begrenzung für den Smoke-Test.
var benchmarkRepo = Environment.GetEnvironmentVariable("NAUDIT_BENCHMARK_REPO")
    ?? throw new InvalidOperationException("NAUDIT_BENCHMARK_REPO muss auf den Benchmark-Klon zeigen.");
var goldenDir = Path.Combine(benchmarkRepo, "offline", "golden_comments");
var outputPath = Environment.GetEnvironmentVariable("NAUDIT_BENCHMARK_OUTPUT")
    ?? Path.Combine(benchmarkRepo, "offline", "results", "naudit-reviews.json");
// Gesetzt, aber unlesbar ⇒ Abbruch statt stiller Default (siehe EnvNumbers).
var limit = EnvNumbers.Read("NAUDIT_BENCHMARK_LIMIT", int.MaxValue);
var pause = TimeSpan.FromSeconds(EnvNumbers.Read("NAUDIT_BENCHMARK_PAUSE_SECONDS", 20));

var config = new ConfigurationBuilder()
    .AddJsonFile("benchmark.appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var warnings = new WarningCollector();

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(config);
services.AddSingleton(warnings);
// Nur der Sammler — kein Konsolen-Provider (spart das Paket Microsoft.Extensions.Logging.Console;
// der Runner gibt Warnungen ohnehin selbst je Review aus).
services.AddLogging(b =>
{
    b.SetMinimumLevel(LogLevel.Warning);
    b.AddProvider(new CollectingLoggerProvider(warnings));
});
services.AddNauditDatabase(config);
services.AddNauditInfrastructure(config);
services.AddBenchmarkCapture();

using var provider = services.BuildServiceProvider();

// Schema anlegen. Im Web-Host erledigt das der DbSettingsLoader vor dem Host-Bau; hier gibt es
// ihn nicht. Ohne Migration scheiterte JEDER DB-Zugriff der Pipeline. Die Audit-Senke bliebe dabei
// stumm (ReviewService.RecordAuditAsync schluckt ohne Log, EfReviewAuditSink loggt nur den
// Erfolgsfall) — sichtbar würde es über die DB-Pfade von Review-Gedächtnis (DbReviewMemory) und
// Architektur-Profil (DistillingReviewGuidelines), die ihre Fehler beide als Warning loggen und
// damit über den WarningCollector alle 50 Reviews als auffällig melden.
using (var migrationScope = provider.CreateScope())
    await migrationScope.ServiceProvider.GetRequiredService<NauditDbContext>().Database.MigrateAsync();

// Preflight: erst alles parsen, dann erst reviewen — ein Tippfehler im Datensatz soll
// nicht nach dreißig Reviews auffallen.
var entries = GoldenDataset.Load(goldenDir);
Console.WriteLine($"{entries.Count} Einträge geladen, {entries.Select(e => e.ProjectId).Distinct().Count()} Projekte.");

var store = new ResultStore(outputPath);
var done = store.CompletedUrls;
var todo = entries.Where(e => !done.Contains(e.Url)).Take(limit).ToList();
Console.WriteLine($"{done.Count} bereits erledigt, {todo.Count} zu tun.");

var capture = provider.GetRequiredService<ReviewCapture>();
var index = 0;

foreach (var entry in todo)
{
    index++;
    Console.WriteLine($"[{index}/{todo.Count}] {entry.ProjectId}#{entry.Number} — {entry.PrTitle}");
    capture.Reset();
    warnings.Drain();   // Reste des Vorgängers verwerfen

    var sw = Stopwatch.StartNew();
    string? error = null;
    try
    {
        using var scope = provider.CreateScope();
        var reviewService = scope.ServiceProvider.GetRequiredService<ReviewService>();
        // Trigger = Ci: das Roundtrip-Limit ist hier bedeutungslos, aber die Absicht soll im Code stehen.
        var request = new ReviewRequest(entry.ProjectId, entry.Number, entry.PrTitle, null, ReviewTrigger.Ci);
        await reviewService.ReviewAsync(request);
    }
    catch (Exception ex)
    {
        error = ex.Message;
    }
    sw.Stop();

    var collected = warnings.Drain();
    var captured = capture.Last;
    if (captured is null)
    {
        // Kein PostReviewAsync ⇒ kein Review. Nicht speichern, damit der nächste Lauf es wiederholt.
        Console.WriteLine($"    FEHLGESCHLAGEN: {error ?? "kein Review erzeugt (leerer Diff?)"}");
        continue;
    }

    var diagnostics = new ReviewDiagnostics(
        CheckoutRequested: capture.CheckoutRequested,
        CheckoutFailed: capture.CheckoutFailures > 0,
        HeadRef: capture.HeadRef,
        ContextInPrompt: capture.ContextInPrompt,
        GuidelinesInPrompt: capture.GuidelinesInPrompt,
        InputTokens: capture.InputTokens,
        OutputTokens: capture.OutputTokens,
        ChangedFiles: capture.ChangedFiles,
        Warnings: collected,
        DurationSeconds: sw.Elapsed.TotalSeconds,
        Error: error);

    store.Append(new BenchmarkRecord(entry.Url, captured, diagnostics));
    Console.WriteLine($"    {captured.Comments.Count} Inline-Kommentare, {captured.Verdict}, {sw.Elapsed.TotalSeconds:F0}s, " +
        $"{diagnostics.ChangedFiles} Dateien, " +
        $"{diagnostics.InputTokens?.ToString() ?? "?"}/{diagnostics.OutputTokens?.ToString() ?? "?"} Tokens");
    foreach (var reason in ReviewAnomalies.Of(diagnostics))
        Console.WriteLine($"    ACHTUNG: {reason}");
    if (diagnostics.PossiblyTruncated)
        Console.WriteLine($"    ACHTUNG: {diagnostics.ChangedFiles} geänderte Dateien — GetChangesAsync holt nur eine Seite " +
            $"(per_page={ReviewDiagnostics.PageLimit}); dieser PR wurde womöglich gekürzt reviewt.");

    if (index < todo.Count)
        await Task.Delay(pause);
}

// Abschlussbericht: was noch fehlt und was auffällig war.
var remaining = entries.Count - store.CompletedUrls.Count;
var suspicious = store.All()
    .Select(r => (r.Url, Reasons: ReviewAnomalies.Of(r.Diagnostics)))
    .Where(x => x.Reasons.Count > 0)
    .ToList();
Console.WriteLine();
Console.WriteLine($"Fertig: {store.CompletedUrls.Count}/{entries.Count}, offen: {remaining}");
if (suspicious.Count > 0)
{
    Console.WriteLine($"ACHTUNG — {suspicious.Count} auffällige Reviews (vor dem Import wiederholen):");
    foreach (var (url, reasons) in suspicious)
        Console.WriteLine($"  {url}: {string.Join(" | ", reasons)}");
}

// Gekürzt reviewte PRs stehen separat: ein Wiederholungslauf sähe dasselbe (die Seitengrenze ist
// eine POC-Grenze von GetChangesAsync, kein transienter Fehler). Sie gehören in die Arbeit, nicht
// in die Wiederholungsliste.
var truncated = store.All().Where(r => r.Diagnostics.PossiblyTruncated).ToList();
if (truncated.Count > 0)
{
    Console.WriteLine($"HINWEIS — {truncated.Count} PRs mit voller Dateiseite (per_page={ReviewDiagnostics.PageLimit}), " +
        "womöglich gekürzt reviewt — in der Arbeit als Grenze nennen:");
    foreach (var r in truncated)
        Console.WriteLine($"  {r.Url}: {r.Diagnostics.ChangedFiles} Dateien");
}
