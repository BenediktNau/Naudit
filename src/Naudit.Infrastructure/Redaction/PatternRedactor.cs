using System.Text.RegularExpressions;
using Naudit.Core.Abstractions;

namespace Naudit.Infrastructure.Redaction;

/// <summary>Deterministischer Default-Redactor: maskiert Secrets/API-Keys/Passwörter, IP-Adressen
/// und E-Mail-Adressen per Regex + konservativer Shannon-Entropie. <b>Line-preserving</b>: arbeitet
/// zeilenweise, lässt Diff-Strukturzeilen (<c>@@</c>/<c>+++</c>/<c>---</c>/<c>diff --git</c>/<c>index</c>)
/// unangetastet und fügt/entfernt nie Zeilen — damit bleibt die New-File-Zeilennummerierung im
/// <c>PromptBuilder</c> (und damit die Inline-Positionen) exakt erhalten. Der rohe Wert taucht nie
/// im Output auf; er wird durch <c>«redacted:&lt;kind&gt;»</c> ersetzt.</summary>
public sealed class PatternRedactor(RedactionOptions options) : IPromptRedactor
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2); // ReDoS-Schutz

    private static Regex R(string pattern, RegexOptions extra = RegexOptions.None)
        => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled | extra, RegexTimeout);

    // Strukturierte Pattern (spezifisch → generisch); der Match wird komplett durch «redacted:<kind>» ersetzt.
    private static readonly (string Kind, Regex Pattern)[] Rules =
    [
        ("token",       R(@"AKIA[0-9A-Z]{16}")),                                   // AWS Access Key ID
        ("token",       R(@"ghp_[A-Za-z0-9]{36}")),                                // GitHub PAT (classic)
        ("token",       R(@"github_pat_[A-Za-z0-9_]{22,}")),                       // GitHub PAT (fine-grained)
        ("token",       R(@"xox[baprs]-[A-Za-z0-9-]{10,}")),                       // Slack
        ("token",       R(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")), // JWT
        ("private-key", R(@"-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----")),
        ("email",       R(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")),
        ("ip",          R(@"\b(?:(?:25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])\b")),
        ("ip",          R(@"\b(?:[A-Fa-f0-9]{1,4}:){7}[A-Fa-f0-9]{1,4}\b")),      // IPv6 (Vollform)
    ];

    // Generische Zuweisung password|secret|api_key|… = "wert" → nur die Wertgruppe maskieren
    // (fängt auch kurze, niedrig-entropische Passwörter, die der Entropie-Pass verfehlt).
    // (?<![\w-]) erzwingt eine linke Grenze, damit Suffixe in normalen Bezeichnern (z. B. authToken)
    // nicht fälschlich greifen; (?<kq>["']?) erlaubt zitierte JSON-Keys wie "password": "…".
    private static readonly Regex Assignment = R(
        @"(?<![\w-])(?<kq>[""']?)(?<key>password|passwd|pwd|secret|api[-_]?key|access[-_]?key|token)(\k<kq>)(?<sep>\s*[:=]\s*)(?<q>[""']?)(?<val>[^\s""',;]+)(\k<q>)",
        RegexOptions.IgnoreCase);

    // Token-artige Substrings für den Entropie-Fallback.
    private static readonly Regex TokenLike = R(@"[A-Za-z0-9+/=_-]+");

    public Task<string> RedactAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(text))
            return Task.FromResult(text);

        // Auf '\n' splitten (Zeilenanzahl bleibt erhalten); ein evtl. '\r' bleibt Teil des Zeileninhalts.
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsStructural(lines[i]))
                continue;
            lines[i] = RedactLine(lines[i]);
        }
        return Task.FromResult(string.Join('\n', lines));
    }

    private string RedactLine(string line)
    {
        // 1) Generische Zuweisung: nur den Wert ersetzen, Schlüssel/Trenner/Quotes behalten
        //    (inkl. evtl. Key-Quotes kq, damit "password": "…" als "password": "«…»" erhalten bleibt).
        line = Assignment.Replace(line, m =>
            $"{m.Groups["kq"].Value}{m.Groups["key"].Value}{m.Groups["kq"].Value}{m.Groups["sep"].Value}{m.Groups["q"].Value}«redacted:secret»{m.Groups["q"].Value}");

        // 2) Strukturierte Pattern.
        foreach (var (kind, rx) in Rules)
            line = rx.Replace(line, $"«redacted:{kind}»");

        // 3) Entropie-Fallback für verbleibende token-artige Reste.
        line = TokenLike.Replace(line, m =>
        {
            var tok = m.Value;
            if (tok.Length < options.MinEntropyTokenLength || !HasDigitAndLetter(tok))
                return tok;
            return ShannonEntropy(tok) >= options.EntropyThreshold ? "«redacted:secret»" : tok;
        });

        return line;
    }

    // Diff-Struktur-/Metazeilen werden nie verändert (defensiv; GitLab/GitHub liefern Hunks ab @@).
    private static bool IsStructural(string line)
        => line.StartsWith("@@", StringComparison.Ordinal)
        || line.StartsWith("--- ", StringComparison.Ordinal)
        || line.StartsWith("+++ ", StringComparison.Ordinal)
        || line.StartsWith("diff --git", StringComparison.Ordinal)
        || line.StartsWith("index ", StringComparison.Ordinal)
        || line.StartsWith("\\ No newline", StringComparison.Ordinal);

    // Secrets mischen praktisch immer Ziffern und Buchstaben — schließt wort-artige Identifier aus.
    private static bool HasDigitAndLetter(string s)
    {
        bool digit = false, letter = false;
        foreach (var c in s)
        {
            if (char.IsDigit(c)) digit = true;
            else if (char.IsLetter(c)) letter = true;
            if (digit && letter) return true;
        }
        return false;
    }

    // Shannon-Entropie in Bits pro Zeichen.
    private static double ShannonEntropy(string s)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in s)
            counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1;

        double entropy = 0;
        double len = s.Length;
        foreach (var count in counts.Values)
        {
            var p = count / len;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }
}
