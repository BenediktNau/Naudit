namespace Naudit.Infrastructure.Analytics;

/// <summary>Zentrale String-Werte des Resolution-Trackings. Writer, Command-Service, Endpoints
/// und Aggregation vergleichen dieselben Werte — verstreute Literale wären tippfehleranfällig
/// (z. B. "WebUI" vs. "WebUi" bräche die Präzedenz-Regel still, ohne Compiler-Fehler).</summary>
public static class ResolutionValues
{
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";

    /// <summary>Signal-Quellen; PR 4 ergänzt "Checkbox"/"Emoji".</summary>
    public static class Sources
    {
        public const string Command = "Command";
        public const string WebUi = "WebUi";
        public const string Llm = "Llm";
    }
}
