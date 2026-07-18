// src/Naudit.Infrastructure/Git/FpReplyCommand.cs
using System.Text.RegularExpressions;

namespace Naudit.Infrastructure.Git;

/// <summary>Ein erkanntes FP-Antwort-Kommando: der Grund (Rest der Zeile) oder null.</summary>
public sealed record ParsedFpCommand(string? Reason);

/// <summary>Parst die Antwort auf einen Inline-Kommentar: "@naudit fp|false-positive &lt;grund&gt;"
/// (case-insensitiv, am Zeilenanfang). Rest der ersten Zeile = Grund. Kein Match ⇒ null.
/// Plattform-agnostisch — GitHub- wie GitLab-Kommentar-Bodies laufen hier durch.</summary>
public static class FpReplyCommand
{
    // ^ am (getrimmten) Zeilenanfang; fp|false-positive als ganzes Wort (\b); Rest = Grund.
    private static readonly Regex Pattern = new(
        @"^@naudit\s+(?:fp|false-positive)\b[ \t]*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ParsedFpCommand? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        // Nur die erste Zeile betrachten — das Kommando steht vorn; ein etwaiger Grund ist der Rest DIESER Zeile.
        var line = body.Trim();
        var nl = line.IndexOf('\n');
        if (nl >= 0)
            line = line[..nl];
        line = line.TrimEnd('\r').Trim();

        var m = Pattern.Match(line);
        if (!m.Success)
            return null;

        var reason = m.Groups[1].Value.Trim();
        return new ParsedFpCommand(reason.Length == 0 ? null : reason);
    }
}
