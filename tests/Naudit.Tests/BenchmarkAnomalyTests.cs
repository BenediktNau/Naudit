using Naudit.Benchmark;

namespace Naudit.Tests;

/// <summary>Die Auffälligkeits-Regeln des Runners. Sie entscheiden, welches Review wiederholt statt
/// importiert wird — der einzige Schutz davor, dass ein stumm degradiertes Review als "nichts
/// gefunden" in die Precision/Recall-Zahl eingeht.</summary>
public class BenchmarkAnomalyTests
{
    private static ReviewDiagnostics Good() => new(
        CheckoutRequested: true, CheckoutFailed: false, HeadRef: "refs/pull/1/head",
        ContextInPrompt: true, GuidelinesInPrompt: true, InputTokens: 1000, OutputTokens: 200,
        Warnings: [], DurationSeconds: 12.5, Error: null);

    [Fact]
    public void Ein_vollstaendiges_Review_ist_unauffaellig()
        => Assert.Empty(ReviewAnomalies.Of(Good()));

    [Fact]
    public void Fehlender_Repo_Kontext_ist_auffaellig()
    {
        var reasons = ReviewAnomalies.Of(Good() with { ContextInPrompt = false });
        Assert.Contains(reasons, r => r.Contains("Repo-Kontext"));
    }

    [Fact]
    public void Fehlendes_Architektur_Profil_ist_auffaellig()
    {
        var reasons = ReviewAnomalies.Of(Good() with { GuidelinesInPrompt = false });
        Assert.Contains(reasons, r => r.Contains("Architektur-Profil"));
    }

    [Fact]
    public void Gescheiterter_Checkout_ist_auffaellig()
    {
        var reasons = ReviewAnomalies.Of(Good() with { CheckoutFailed = true });
        Assert.Contains(reasons, r => r.Contains("Checkout fehlgeschlagen"));
    }

    [Fact]
    public void Nicht_angefragter_Checkout_ist_auffaellig()
    {
        var reasons = ReviewAnomalies.Of(Good() with { CheckoutRequested = false });
        Assert.Contains(reasons, r => r.Contains("kein Checkout angefragt"));
    }

    [Fact]
    public void Fehler_und_Warnungen_sind_auffaellig()
    {
        Assert.Contains(ReviewAnomalies.Of(Good() with { Error = "boom" }), r => r.Contains("boom"));
        Assert.Contains(ReviewAnomalies.Of(Good() with { Warnings = ["git fetch schlug fehl"] }),
            r => r.Contains("git fetch"));
    }

    [Fact]
    public void Mehrere_Maengel_werden_alle_gemeldet()
    {
        var reasons = ReviewAnomalies.Of(Good() with { CheckoutFailed = true, ContextInPrompt = false, GuidelinesInPrompt = false });
        Assert.Equal(3, reasons.Count);
    }
}
