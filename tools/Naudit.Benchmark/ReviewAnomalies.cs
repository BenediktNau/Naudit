namespace Naudit.Benchmark;

/// <summary>Die Regeln, nach denen ein Review als auffällig gilt — wortgleich zu denen, nach denen
/// tools/benchmark/import_reviews.py den Import verweigert. Ein auffälliges Review wird wiederholt,
/// nicht importiert: Naudits Pipeline ist fail-open, ein degradierter Lauf sähe im Ergebnis nur wie
/// ein schwächeres Review aus und verschöbe damit still die Zahl der Bachelorarbeit.</summary>
public static class ReviewAnomalies
{
    public static IReadOnlyList<string> Of(ReviewDiagnostics d)
    {
        var reasons = new List<string>();
        if (d.Error is not null)
            reasons.Add($"Fehler: {d.Error}");
        if (!d.CheckoutRequested)
            reasons.Add("kein Checkout angefragt — Review lief ohne Repo-Kontext");
        if (d.CheckoutFailed)
            reasons.Add("Checkout fehlgeschlagen — diff-only, ohne Repo-Kontext und ohne frisches Profil");
        if (!d.ContextInPrompt)
            reasons.Add("kein Repo-Kontext im Prompt — Kontextsammlung kam leer zurück");
        if (!d.GuidelinesInPrompt)
            reasons.Add("kein Architektur-Profil im Prompt — Destillation leer oder gescheitert");
        if (d.Warnings.Count > 0)
            reasons.Add("Warnungen der Pipeline: " + string.Join(" | ", d.Warnings));
        return reasons;
    }
}
