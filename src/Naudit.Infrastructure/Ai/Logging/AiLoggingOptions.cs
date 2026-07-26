namespace Naudit.Infrastructure.Ai.Logging;

/// <summary>Config-Sektion Naudit:Ai:Logging — steuert die Prompt-/Kommunikations-Middleware
/// (PromptLoggingBehavior). Default aus: kein Overhead, keine gespeicherten Prompts, solange
/// niemand es einschaltet. Prompts enthalten (redigierten) Quellcode ⇒ Persistenz ist opt-in
/// und die WebUI zeigt die Volltexte nur Admins.</summary>
public sealed class AiLoggingOptions
{
    /// <summary>Master-Schalter. Aus ⇒ der IChatClient wird gar nicht erst mit MediatorChatClient
    /// umhüllt, der Aufruf läuft wie bisher direkt.</summary>
    public bool Enabled { get; set; }

    /// <summary>System- und User-Prompt in Log/DB aufnehmen (der eigentliche Prompt-Volltext).
    /// Aus ⇒ nur Metadaten (Modell, Token, Latenz, Zeichenzahl).</summary>
    public bool IncludePrompts { get; set; } = true;

    /// <summary>Die rohe LLM-Antwort (vor dem JSON-Parsen) in Log/DB aufnehmen.</summary>
    public bool IncludeResponse { get; set; } = true;

    /// <summary>Transcript pro Aufruf in die DB schreiben (WebUI-Sichtbarkeit im Review-Detail).
    /// Aus ⇒ nur strukturiertes ILogger-Logging, nichts in der DB.</summary>
    public bool Persist { get; set; } = true;

    /// <summary>Obergrenze für gespeicherte Prompt-/Antwort-Länge (Zeichen); 0 = unbegrenzt.
    /// Schützt die DB vor Riesen-Diffs.</summary>
    public int MaxCharsPerField { get; set; }
}
