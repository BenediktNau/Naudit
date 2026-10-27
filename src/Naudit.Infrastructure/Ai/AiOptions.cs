using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Naudit.Infrastructure.Ai;

public enum AiProvider { Anthropic, Ollama, OpenAICompatible, ClaudeCode }

/// <summary>Session-Routing pro Review: globaler Provider | autor-gebunden. Eine Session wird nie
/// für das Review eines fremden MRs/PRs benutzt (der frühere RoundRobin-Pool ist entfernt).</summary>
[TypeConverter(typeof(SessionRoutingConverter))]
public enum SessionRouting { Single, Author }

/// <summary>Bindet den Alt-Wert "RoundRobin" (entfernter Pool-Modus) auf Author, statt den Start in den
/// Recovery-Modus zu zwingen — eine Alt-Konfiguration (env oder DB-Zeile) bleibt so lauffähig, und
/// keine Session wird mehr fremd genutzt. Die Warnung dazu liefert AiOptions.LegacyWarnings.</summary>
public sealed class SessionRoutingConverter : EnumConverter
{
    public const string LegacyRoundRobin = "RoundRobin";

    public SessionRoutingConverter() : base(typeof(SessionRouting)) { }

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        => value is string s && string.Equals(s.Trim(), LegacyRoundRobin, StringComparison.OrdinalIgnoreCase)
            ? SessionRouting.Author
            : base.ConvertFrom(context, culture, value);
}

/// <summary>Wo Abo-Session-Läufe (Author) ausgeführt werden: in-process (heutiges
/// Verhalten) oder in Geschwister-Containern pro Account über den Host-Docker-Socket.</summary>
public enum SessionSandbox { None, Docker }

public sealed class AiOptions
{
    public AiProvider Provider { get; set; } = AiProvider.Ollama;
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }
    /// <summary>Ollama-Base-URL oder Base-URL eines OpenAI-kompatiblen Dienstes (z. B. NVIDIA).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Gesamt-Timeout einer LLM-Anfrage in Sekunden. Default 600 (10 min): große Diffs
    /// bzw. Thinking-Modelle (z. B. Ollama qwen3.5) sprengen sonst den HttpClient-Standard von 100 s.</summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>Wie der Chat-Client pro Review gewählt wird (Naudit:Ai:SessionRouting).
    /// Single = globaler Provider (Default, heutiges Verhalten); Author = autor-gebunden.
    /// Alt-Wert RoundRobin ⇒ Author (siehe SessionRoutingConverter).</summary>
    public SessionRouting SessionRouting { get; set; } = SessionRouting.Single;

    /// <summary>Naudit:Ai:SessionSandbox — Default None = heutiger In-Process-Runner. Docker greift
    /// nur für Author-Routing; im Single-Modus (globaler Provider) ist es bedeutungslos.
    /// Sub-Optionen unter Naudit:Ai:Sandbox (SessionSandboxOptions).</summary>
    public SessionSandbox SessionSandbox { get; set; } = SessionSandbox.None;

    /// <summary>Warnungen zu entfernten Alt-Werten, die still umgedeutet werden (Start-Log + Settings-Seite).</summary>
    public static IReadOnlyList<string> LegacyWarnings(IConfiguration config)
    {
        var routing = config["Naudit:Ai:SessionRouting"];
        return string.Equals(routing?.Trim(), SessionRoutingConverter.LegacyRoundRobin, StringComparison.OrdinalIgnoreCase)
            ? ["Naudit:Ai:SessionRouting=RoundRobin wird nicht mehr unterstützt (Session-Pool entfernt) — "
                + "es greift Author: jede Session reviewt nur noch MRs/PRs ihres eigenen Autors. "
                + "Bitte auf Author oder Single umstellen."]
            : [];
    }
}
