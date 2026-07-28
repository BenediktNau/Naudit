namespace Naudit.Core.Review;

/// <summary>Hinweis auf die Antwort-Kommandos (@naudit fp / @naudit ok), der an die GEPOSTETE
/// Kopie eines Reviews gehaengt wird. Inline als HTML-Kommentar: GitHub und GitLab verschlucken
/// den beim Rendern, liefern ihn aber im rohen Body der API aus — genau der Weg, auf dem ein
/// AI-Agent im Thread von den Kommandos erfaehrt, ohne dass ein Mensch Rauschen sieht. In der
/// Summary stattdessen ein zugeklappter details-Block, damit die Funktion einmal pro Review auch
/// fuer Menschen auffindbar bleibt.</summary>
public static class ReviewCommandHint
{
    /// <summary>Unsichtbarer Block fuer jeden Inline-Kommentar. Bewusst ASCII und ohne "--":
    /// ein doppelter Bindestrich im Inneren wuerde den HTML-Kommentar aufbrechen.</summary>
    public static string Inline(ReviewResolutionOptions options)
    {
        if (!options.RenderHint)
            return string.Empty;

        // Ohne Resolution-Tracking wird "ok" still verworfen — dann gar nicht erst nennen.
        var ok = options.Enabled
            ? "\n  @naudit ok <text>    - Finding angenommen/umgesetzt."
            : string.Empty;

        return "\n\n<!-- naudit:commands\n"
             + "Antworte AUF DIESEN KOMMENTAR (Reply im selben Thread, kein neuer Top-Level-Kommentar).\n"
             + "Erste Zeile der Antwort, genau eines:\n"
             + "  @naudit fp <grund>   - Fehlalarm; Naudit merkt sich das dauerhaft fuer dieses Projekt."
             + ok + "\n"
             + "Nur Repo-Mitglieder (Developer/Collaborator aufwaerts) sind autorisiert.\n"
             + "-->";
    }

    /// <summary>Zugeklappter Block fuer den Summary-Kommentar — dieselbe Information fuer Menschen.</summary>
    public static string Summary(ReviewResolutionOptions options)
    {
        if (!options.RenderHint)
            return string.Empty;

        var ok = options.Enabled
            ? "\n- `@naudit ok <text>` — Finding angenommen/umgesetzt."
            : string.Empty;

        return "\n\n<details><summary>🤖 Naudit-Kommandos</summary>\n\n"
             + "Antworte im Thread eines Inline-Kommentars — erste Zeile der Antwort:\n\n"
             + "- `@naudit fp <grund>` — Fehlalarm. Naudit merkt sich das für dieses Projekt und meidet den Fund künftig."
             + ok + "\n\n"
             + "Nur Repo-Mitglieder (Developer/Collaborator aufwärts) sind autorisiert. Ein neuer "
             + "Top-Level-Kommentar wird nicht ausgewertet, es muss eine Antwort auf den Kommentar sein.\n"
             + "</details>";
    }
}
