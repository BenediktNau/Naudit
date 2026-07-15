namespace Naudit.Infrastructure.Ai;

/// <summary>Autor-Sessions ("bring your own subscription"): Reviews eigener MRs laufen über den
/// im Profil hinterlegten Claude-Code-Token des Autors. Section Naudit:Ai:AuthorSessions.</summary>
public sealed class AuthorSessionsOptions
{
    public bool Enabled { get; set; }

    /// <summary>So lange wird eine gescheiterte Session übersprungen (Pro/Max-Limits arbeiten
    /// in 5-h-Fenstern; 30 min ist ein pragmatischer Wiederanlauf-Takt).</summary>
    public int CooldownMinutes { get; set; } = 30;

    /// <summary>CLI-Modell(-Alias) für Autor-Läufe — bewusst getrennt von Naudit:Ai:Model,
    /// das eine nur dem globalen Provider bekannte Id sein kann (z. B. Ollama-Modellname).</summary>
    public string Model { get; set; } = "sonnet";
}
