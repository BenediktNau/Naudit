// src/Naudit.Infrastructure/Git/FpReplyCommand.cs
using System.Text.RegularExpressions;

namespace Naudit.Infrastructure.Git;

/// <summary>Art des Antwort-Kommandos an einem Inline-Kommentar.</summary>
public enum ReviewCommandKind { FalsePositive, Accept }

/// <summary>Ein erkanntes Antwort-Kommando: Art + optionaler Grund (Rest der Zeile).</summary>
public sealed record ParsedReviewCommand(ReviewCommandKind Kind, string? Reason);

/// <summary>Parst die Antwort auf einen Inline-Kommentar: "@naudit fp|false-positive &lt;grund&gt;"
/// (⇒ FalsePositive) oder "@naudit ok|angenommen|accepted &lt;text&gt;" (⇒ Accept), case-insensitiv,
/// am Zeilenanfang, Verb durch Whitespace vom Rest getrennt oder Zeilenende. Kein Match ⇒ null.
/// Plattform-agnostisch — GitHub- wie GitLab-Kommentar-Bodies laufen hier durch.</summary>
public static class FpReplyCommand
{
    // ^ am (getrimmten) Zeilenanfang; das Verb muss durch Whitespace vom Rest getrennt sein
    // (oder die Zeile endet direkt danach) — kein \b nötig, [ \t]+-oder-Ende lehnt "fp-something" schon ab.
    private static readonly Regex Pattern = new(
        @"^@naudit\s+(?<verb>fp|false-positive|ok|angenommen|accepted)(?:[ \t]+(?<rest>.*))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ParsedReviewCommand? TryParse(string? body)
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

        var verb = m.Groups["verb"].Value.ToLowerInvariant();
        var kind = verb is "fp" or "false-positive" ? ReviewCommandKind.FalsePositive : ReviewCommandKind.Accept;
        var rest = m.Groups["rest"].Value.Trim();
        return new ParsedReviewCommand(kind, rest.Length == 0 ? null : rest);
    }
}
