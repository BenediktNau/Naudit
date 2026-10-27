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
    // Präfix-Token (GitHub/GitLab/Anthropic/OpenAI/Slack/AWS) tragen ihre Erkennung im Präfix selbst
    // und brauchen deshalb weder Schlüssel-Kontext noch Entropie-Schwelle. Die linke Grenze L
    // verhindert Treffer mitten in Bezeichnern (`desk-layout`, `task-runner`).
    private const string L = @"(?<![A-Za-z0-9_-])";

    private const string PemBegin = @"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----";
    private const string PemEnd = @"-----END (?:[A-Z0-9]+ )*PRIVATE KEY-----";

    private static readonly (string Kind, Regex Pattern)[] Rules =
    [
        ("token",       R(L + @"(?:AKIA|ASIA)[0-9A-Z]{16}")),                      // AWS Access Key ID (dauerhaft/temporär)
        ("token",       R(L + @"gh[pousr]_[A-Za-z0-9]{36,}")),                     // GitHub PAT/OAuth/User/App/Refresh
        ("token",       R(L + @"github_pat_[A-Za-z0-9_]{22,}")),                   // GitHub PAT (fine-grained)
        // GitLab: glpat- (PAT), gldt- (Deploy), glrt- (Runner), glptt- (Trigger), glcbt- (CI-Job),
        // glft- (Feed), glimt- (Incoming Mail), glagent- (K8s-Agent), gloas- (OAuth-Secret),
        // glsoat- (SCIM), glffct- (Feature-Flag-Client), glwt- (Workspace); '.' für die neueren
        // routbaren Token-Formate.
        ("token",       R(L + @"gl(?:pat|dt|rt|ptt|cbt|ft|imt|agent|oas|soat|ffct|wt)-[A-Za-z0-9_.-]{20,}")),
        ("token",       R(L + @"sk-ant-[A-Za-z0-9_-]{20,}")),                      // Anthropic (vor OpenAI!)
        // OpenAI: sk-…, sk-proj-…, sk-svcacct-…, sk-admin-… — der Lookahead verlangt eine Ziffer,
        // damit lange kebab-case-Wörter hinter "sk-" nicht greifen.
        ("token",       R(L + @"sk-(?:proj-|svcacct-|admin-)?(?=[A-Za-z_-]*[0-9])[A-Za-z0-9_-]{20,}")),
        ("token",       R(L + @"xox[baprs]-[A-Za-z0-9-]{10,}")),                   // Slack
        ("token",       R(@"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+")), // JWT
        // Einzeiliger PEM-Block (JSON/.env mit "\n"-Escapes): vom BEGIN bis einschließlich END; ohne
        // END auf derselben Zeile nur der direkt anschließende Base64/"\n"-Lauf — ein schließendes
        // Quote (Header als Code-Konstante) bleibt so stehen. Mehrzeilige Blöcke behandelt
        // RedactAsync über den Block-Zustand.
        ("private-key", R(PemBegin + @"(?:.*?" + PemEnd + @"|(?:\\[rn]|[A-Za-z0-9+/=])*)")),
        ("email",       R(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")),
        ("ip",          R(@"\b(?:(?:25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])\b")),
        ("ip",          R(@"\b(?:[A-Fa-f0-9]{1,4}:){7}[A-Fa-f0-9]{1,4}\b")),      // IPv6 (Vollform)
    ];

    // Generische Zuweisung password|secret|api_key|… = "wert" → nur die Wertgruppe maskieren.
    // Diese Regel ist die EINZIGE, die kurze Secrets fangen kann: bei EntropyThreshold 4.0 erreicht
    // ein Token unter 16 Zeichen die Schwelle nie (log2(16) = 4.0 bei lauter verschiedenen Zeichen),
    // der Entropie-Pass ist dort also konstruktionsbedingt blind.
    // Linke Grenze (?<![A-Za-z0-9]): '_' und '-' zählen bewusst NICHT als Wortzeichen, damit
    // präfixierte Schlüssel greifen (DB_PASSWORD=, MY_DB_CREDENTIAL=, x-token:) — die in .env-,
    // Dockerfile- und Header-Schreibweise übliche Form, die vorher komplett durchrutschte.
    // Ein Buchstabe/eine Ziffer davor blockt weiterhin, damit Suffixe in normalen Bezeichnern
    // (z. B. authToken) nicht fälschlich greifen; der Schlüssel muss zudem unmittelbar vor dem
    // Trenner enden, sonst bliebe `secret_flag = true` hängen.
    // (?<kq>["']?) erlaubt zitierte JSON-Keys wie "password": "…".
    // Der Wert endet auch am Backtick: sonst frisst die Regel in Doku/Prosa den schliessenden
    // Code-Span (`x-token:` wird zu `x-«redacted:secret») und hinterlaesst kaputtes Markdown --
    // ausgerechnet in der Datei, die dieses Verhalten beschreibt.
    // Suffix-Schlüssel (WebhookSecret=, ClientSecret=, OpenAiApiKey:): (?<pre>) nimmt einen
    // Bezeichner-Präfix auf; der Schlüsselname bleibt vollständig erhalten, nur der Wert wird
    // maskiert. Weil damit auch gewöhnlicher Code wie `authToken = lookupValue` matcht, entscheidet
    // zusätzlich der WERT (LooksLikeCode) — siehe RedactLine.
    // (?![=>]) nach dem Trenner: `apiKey == null` und `token => …` sind Vergleich/Lambda, keine Zuweisung.
    private static readonly Regex Assignment = R(
        @"(?<![A-Za-z0-9])(?<kq>[""']?)(?<pre>[A-Za-z0-9_-]*?)(?<key>password|passwd|pwd|passphrase|secret|credentials?|api[-_]?key|access[-_]?key|token)(\k<kq>)(?<sep>\s*[:=](?![=>])\s*)(?<q>[""']?)(?<val>[^\s""',;`]+)(\k<q>)",
        RegexOptions.IgnoreCase);

    // Schlüssel, die auch als Suffix eines Bezeichners (camelCase/PascalCase/zusammengeschrieben)
    // greifen. Bewusst ohne pwd/passwd (zu kurz, zu viele Zufallstreffer) und ohne credential(s)
    // (mehrdeutig, s. u.).
    private static readonly HashSet<string> SuffixKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passphrase", "secret", "token", "apikey", "api_key", "api-key",
        "accesskey", "access_key", "access-key",
    };

    // Literale, die nie ein Secret sind (`WebhookSecret: null`, `var hasToken = true`).
    private static readonly HashSet<string> CodeLiterals = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null", "none", "nil", "undefined", "default", "await", "new",
    };

    // Member-Zugriff `options.ClientSecret`, `request?.Token`, `cfg.Secret!`.
    // Optional mit offenem Indexer am Ende (`context.Request.Headers[` — der Wert endet am Quote).
    private static readonly Regex MemberAccess = R(@"^[A-Za-z_$@][A-Za-z0-9_$]*(?:[?!]?\.[A-Za-z_$][A-Za-z0-9_$]*)+[!?]*\[?$");

    // Reiner Bezeichner ohne Ziffer (`lookupValue`, `ct`, `token`).
    private static readonly Regex PlainIdentifier = R(@"^[A-Za-z_$][A-Za-z_$]*$");

    private static readonly Regex PemBeginRx = R(PemBegin);
    private static readonly Regex PemEndRx = R(PemEnd);

    // Body-Zeile eines mehrzeiligen PEM-Blocks: optionaler Diff-Marker, Einrückung, optionales
    // öffnendes Quote, dann Base64 (oder die Header "Proc-Type:"/"DEK-Info:" verschlüsselter Keys,
    // oder eine Leerzeile), danach höchstens schließendes Quote/Komma/"+"/"\n"-Escape. Alles andere
    // beendet den Block — sonst würde eine Header-KONSTANTE im Code (Parser) den Rest der Datei
    // verschlucken.
    private static readonly Regex PemBodyLine = R(
        @"^(?<pre>[+\- ]?\s*[""'`]?)(?<body>[A-Za-z0-9+/]+={0,2}|(?:Proc-Type|DEK-Info):[^""'`]*)?(?<tail>(?:\\n)?[""'`]?[\s,;+]*)$");

    // Mehrdeutige Schluessel: im Web-Umfeld ist `credentials` ueberwiegend KEIN Secret
    // (`fetch(url, {credentials: 'include'})`, CORS `credentials: true`). Fuer diese Schluessel
    // allein reicht der Name nicht — der Wert muss selbst nach Secret aussehen. Unmittelbare
    // Folge eines Naudit-Funds an diesem PR: die Ergaenzung um `credentials?` haette sonst in
    // jedem React-/fetch-Diff maskiert und damit genau die Ueber-Maskierung erzeugt, die hier
    // abgestellt wird.
    private static readonly HashSet<string> AmbiguousKeys =
        new(StringComparer.OrdinalIgnoreCase) { "credential", "credentials" };

    private const int MinAmbiguousValueLength = 8;

    // Token-artige Substrings für den Entropie-Fallback.
    // '=' ist bewusst NUR als abschließendes Base64-Padding erlaubt ({0,2}), nicht als Zeichen
    // mitten im Token: sonst frisst der Fallback über die Zuweisungsgrenze hinweg und macht aus
    // `KEY=wert` EIN Token. Das hatte zwei Folgen (PR #86): der Schlüsselname — genau der Kontext,
    // an dem ein Leser ein Secret von einem harmlosen Pin unterscheidet — verschwand in der
    // Maskierung, und die Mischung aus Schlüssel- und Wert-Alphabet hob die Entropie künstlich über
    // die Schwelle. Ein öffentlicher 40-stelliger Commit-SHA (3.62 Bits/Zeichen, unter der Schwelle)
    // wurde so als `«redacted:secret»` maskiert, nur weil `OPENGREP_RULES_REF=` davorstand (4.44).
    // Der Wert selbst wird weiterhin auf eigene Rechnung geprüft — echte Secrets bleiben maskiert.
    // Der Lookahead trennt Base64-Padding vom Zuweisungs-Trenner: ein '=' darf nur geschluckt
    // werden, wenn danach KEIN Token-Zeichen mehr folgt. Ohne ihn hätte `={0,2}` den Trenner
    // mitgenommen und ein hochentropischer SCHLÜSSEL wäre samt '=' maskiert worden
    // (`aB3d…qRs7=publicvalue` → `«redacted:secret»publicvalue`) — die oben zugesicherte
    // Zuweisungsgrenze wäre damit genau dort unwahr gewesen, wo sie zählt.
    private static readonly Regex TokenLike = R(@"[A-Za-z0-9+/_-]+={0,2}(?![A-Za-z0-9+/_=-])");

    public Task<string> RedactAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(text))
            return Task.FromResult(text);

        // Auf '\n' splitten (Zeilenanzahl bleibt erhalten); ein evtl. '\r' bleibt Teil des Zeileninhalts.
        var lines = text.Split('\n');
        var inPem = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (IsStructural(lines[i]))
                continue;

            // Mehrzeiliger PEM-Block: Body-Zeilen komplett maskieren (Entropie allein reicht nicht —
            // kurze Schlusszeilen oder Zeilen ohne Ziffer rutschten durch). Diff-Marker bleibt stehen.
            if (inPem)
            {
                if (PemEndRx.IsMatch(lines[i]))
                {
                    inPem = false;
                }
                else if (PemBodyLine.Match(lines[i]) is { Success: true } body)
                {
                    if (body.Groups["body"].Success)
                        lines[i] = body.Groups["pre"].Value + "«redacted:private-key»" + body.Groups["tail"].Value;
                    continue;
                }
                else
                {
                    inPem = false; // kein Body mehr ⇒ Block endet, Zeile normal redigieren
                }
            }
            else if (PemBeginRx.IsMatch(lines[i]) && !PemEndRx.IsMatch(lines[i]))
            {
                inPem = true;
            }

            lines[i] = RedactLine(lines[i]);
        }
        return Task.FromResult(string.Join('\n', lines));
    }

    private string RedactLine(string line)
    {
        // 1) Generische Zuweisung: nur den Wert ersetzen, Schlüssel/Trenner/Quotes behalten
        //    (inkl. evtl. Key-Quotes kq, damit "password": "…" als "password": "«…»" erhalten bleibt).
        line = Assignment.Replace(line, m =>
        {
            var val = m.Groups["val"].Value;
            var key = m.Groups["key"].Value;
            var pre = m.Groups["pre"].Value;

            // Suffix-Treffer (Präfix endet auf Buchstabe/Ziffer, z. B. `Webhook|Secret`) nur für die
            // eindeutigen Schlüssel. `_`/`-` als Präfix-Ende (DB_PASSWORD, x-token) ist der
            // klassische Fall und bleibt unverändert.
            var suffix = pre.Length > 0 && char.IsLetterOrDigit(pre[^1]);
            if (suffix && !SuffixKeys.Contains(key))
                return m.Value;

            // Mehrdeutiger Schlüssel ⇒ der Wert muss die Beweislast tragen.
            if (AmbiguousKeys.Contains(key)
                && (val.Length < MinAmbiguousValueLength || !HasDigitAndLetter(val)))
                return m.Value;

            // Code statt Secret: der WERT ist ein Ausdruck (Aufruf, Member-Zugriff, Literal). Nur
            // unzitierte Werte — ein String-Literal bleibt immer ein Kandidat.
            if (m.Groups["q"].Value.Length == 0
                && LooksLikeCode(val, codeStyle: m.Groups["sep"].Value.Any(char.IsWhiteSpace), suffix))
                return m.Value;

            return $"{m.Groups["kq"].Value}{pre}{key}{m.Groups["kq"].Value}{m.Groups["sep"].Value}{m.Groups["q"].Value}«redacted:secret»{m.Groups["q"].Value}";
        });

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

    /// <summary>Heuristik "Wert ist Code, kein Secret". <paramref name="codeStyle"/> = Trenner mit
    /// Leerraum (`x = y`, `x: y`) — die Schreibweise von Quellcode/YAML; `.env`/INI/CLI (`KEY=wert`)
    /// ist dagegen Konfiguration, dort bleibt ein bezeichner-artiger Wert ein Secret-Kandidat.</summary>
    private static bool LooksLikeCode(string val, bool codeStyle, bool suffix)
    {
        // Schließende Klammern gehören zum umgebenden Ausdruck (`cancellationToken: ct)`).
        var core = val.TrimEnd(')', ']', '}');
        if (!core.Any(char.IsLetterOrDigit))
            return true;                                   // reine Interpunktion (`$`, `@`, `{`)
        if (val[0] is '{' or '<')
            return true;                                   // JSX/Template `{hasToken}`, Platzhalter `<secret>`
        if (val.StartsWith("${", StringComparison.Ordinal))
            return true;                                   // `${{ secrets.X }}`, `${DB_PASSWORD}`: Referenz, kein Wert
        if (CodeLiterals.Contains(core))
            return true;                                   // null/true/false, `await …`, `new …`
        if (val.Contains('('))
            return true;                                   // Aufruf: GetSecret(), vault.GetAsync(…)
        if (codeStyle && MemberAccess.IsMatch(core))
            return true;                                   // options.ClientSecret
        // Suffix-Schlüssel matchen gewöhnliche Bezeichner (authToken, cancellationToken) — dort ist
        // ein ziffernloser Bezeichner als Wert eine Variable, kein Secret.
        return suffix && codeStyle && PlainIdentifier.IsMatch(core);
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
