using System.Text.RegularExpressions;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Core.Review;

namespace Naudit.Infrastructure.Context;

/// <summary>Sprachagnostischer Kontext-Sammler: schneidet umgebenden Code, Call-Sites und einen
/// Repo-Überblick aus dem ausgecheckten Baum. Regex + Einrückung, Precision vor Recall.
/// Fehler je Datei werden geschluckt (weiter mit der nächsten); der Aufrufer degradiert bei
/// Gesamtfehler auf diff-only.</summary>
public sealed class WorkspaceContextCollector(ReviewContextOptions options) : IContextCollector
{
    // Obergrenze, wie weit die Block-Heuristik pro Richtung scannt (Schutz vor Runaway).
    private const int BlockScanLimit = 400;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2); // ReDoS-Schutz (wie PatternRedactor)

    private static Regex R(string pattern)
        => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);

    public Task<ReviewContext> CollectAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = workspace.RootPath;

        var environments = CollectEnvironments(root, changes);
        var usages = CollectUsages(root, changes);

        var ctx = new ReviewContext(environments, usages, null);
        return Task.FromResult(ctx);
    }

    // ---- Umgebung ---------------------------------------------------------
    private IReadOnlyList<FileEnvironment> CollectEnvironments(string root, IReadOnlyList<CodeChange> changes)
    {
        var parsed = DiffParser.Parse(changes);
        var result = new List<FileEnvironment>();

        foreach (var change in changes)
        {
            var abs = SafeResolve(root, change.FilePath);
            if (abs is null || !File.Exists(abs))
                continue;                       // gelöschte/außerhalb liegende Datei -> überspringen

            string[] lines;
            try { lines = File.ReadAllLines(abs); }
            catch { continue; }                 // binär/unlesbar -> überspringen

            if (lines.Length <= options.FullFileMaxLines)
            {
                result.Add(new FileEnvironment(change.FilePath, 1, string.Join('\n', lines), IsFullFile: true));
                continue;
            }

            // Große Datei: Anker = hinzugefügte New-File-Zeilen aus dem Diff (Map-Wert null).
            var anchors = parsed.TryGetValue(change.FilePath, out var map)
                ? map.Where(kv => kv.Value is null).Select(kv => kv.Key).OrderBy(x => x).ToList()
                : [];
            foreach (var (start, end) in MergeRanges(anchors.Select(a => ExpandOne(lines, a)).ToList()))
            {
                var slice = string.Join('\n', lines[(start - 1)..end]);
                result.Add(new FileEnvironment(change.FilePath, start, slice, IsFullFile: false));
            }
        }

        return result;
    }

    // Erweitert einen Anker auf den umgebenden Block: rückwärts zur nächsten, weniger eingerückten
    // Deklarationszeile (Blockkopf), vorwärts bis die Einrückung wieder auf dieses Niveau fällt.
    private (int Start, int End) ExpandOne(string[] lines, int anchor)
    {
        int idx = Math.Clamp(anchor - 1, 0, lines.Length - 1);
        int baseIndent = IndentOf(lines[idx]);

        int start = idx;
        for (int i = idx - 1; i >= 0 && idx - i <= BlockScanLimit; i--)
        {
            if (lines[i].Trim().Length == 0) continue;
            int ind = IndentOf(lines[i]);
            if (ind < baseIndent && LooksLikeDeclaration(lines[i]))
            {
                start = i;
                baseIndent = ind;
                break;
            }
        }

        int end = idx;
        for (int i = idx + 1; i < lines.Length && i - idx <= BlockScanLimit; i++)
        {
            if (lines[i].Trim().Length == 0) { end = i; continue; }
            if (IndentOf(lines[i]) <= baseIndent) { end = i; break; }
            end = i;
        }

        // Mindest-Fallback-Fenster ± BlockPadLines um den Anker (0 ⇒ reine Heuristik).
        start = Math.Min(start, Math.Max(0, idx - options.BlockPadLines));
        end = Math.Max(end, Math.Min(lines.Length - 1, idx + options.BlockPadLines));
        return (start + 1, end + 1);            // 1-basiert, inklusiv
    }

    private static IReadOnlyList<(int Start, int End)> MergeRanges(List<(int Start, int End)> ranges)
    {
        if (ranges.Count == 0) return ranges;
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int End)> { ranges[0] };
        foreach (var r in ranges.Skip(1))
        {
            var last = merged[^1];
            if (r.Start <= last.End + 1)
                merged[^1] = (last.Start, Math.Max(last.End, r.End));
            else
                merged.Add(r);
        }
        return merged;
    }

    private static int IndentOf(string line)
    {
        int n = 0;
        while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
        return n;
    }

    private static bool LooksLikeDeclaration(string line)
        => DeclarationPatterns.Any(rx => rx.IsMatch(line));

    // ---- Symbol-Deklarationsmuster (auch für Blockkopf-Erkennung) --------
    // Keyword-Deklaration (def/function/func/fn/sub NAME), Typ-Deklaration
    // (class/interface/struct/record/enum/trait NAME) und C-Familien-Signatur (… NAME(...) {?).
    // Gruppe "name" = deklarierter Bezeichner. Timeout schützt die lazy C-Signatur vor ReDoS.
    private static readonly Regex[] DeclarationPatterns =
    [
        R(@"\b(?:def|function|func|fn|sub)\s+(?<name>[A-Za-z_]\w*)"),
        R(@"\b(?:class|interface|struct|record|enum|trait)\s+(?<name>[A-Za-z_]\w*)"),
        R(@"^[\w\s,<>\[\]\.\?]*?\b(?<name>[A-Za-z_]\w*)\s*\([^;]*\)\s*\{?\s*$"),
    ];

    // ---- Call-Sites -------------------------------------------------------
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "using", "foreach", "lock",
        "return", "new", "else", "do", "try", "finally", "await", "throw",
    };

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", "dist", "build", "target",
        "vendor", "packages", ".vs", ".idea",
    };

    private const long MaxFileBytes = 512 * 1024;

    private IReadOnlyList<SymbolUsage> CollectUsages(string root, IReadOnlyList<CodeChange> changes)
    {
        var symbols = ExtractSymbols(changes);
        if (symbols.Count == 0) return [];

        var declaringFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in changes)
        {
            var abs = SafeResolve(root, c.FilePath);
            if (abs is not null) declaringFiles.Add(abs);
        }

        var files = EnumerateSourceFiles(root).ToList();     // deterministisch sortiert
        var usages = new List<SymbolUsage>();

        foreach (var symbol in symbols)
        {
            var rx = R($@"\b{Regex.Escape(symbol)}\b");
            int found = 0;
            foreach (var abs in files)
            {
                if (found >= options.MaxUsagesPerSymbol) break;
                if (declaringFiles.Contains(abs)) continue;   // Deklarationsdatei überspringen

                string[] lines;
                try { lines = File.ReadAllLines(abs); }
                catch { continue; }

                for (int i = 0; i < lines.Length && found < options.MaxUsagesPerSymbol; i++)
                {
                    if (!rx.IsMatch(lines[i])) continue;
                    int lo = Math.Max(0, i - options.UsageSnippetLines);
                    int hi = Math.Min(lines.Length - 1, i + options.UsageSnippetLines);
                    var snippet = string.Join('\n', lines[lo..(hi + 1)]);
                    var rel = Path.GetRelativePath(root, abs).Replace('\\', '/');
                    usages.Add(new SymbolUsage(symbol, rel, i + 1, snippet));
                    found++;
                }
            }
        }

        return usages;
    }

    // Zieht Bezeichner aus hinzugefügten (+) Diff-Zeilen über den Deklarations-Regex-Katalog.
    private static IReadOnlyList<string> ExtractSymbols(IReadOnlyList<CodeChange> changes)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            foreach (var raw in change.Diff.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] != '+' || line.StartsWith("+++", StringComparison.Ordinal))
                    continue;
                var added = line[1..];
                foreach (var rx in DeclarationPatterns)
                {
                    var m = rx.Match(added);
                    if (!m.Success) continue;
                    var name = m.Groups["name"].Value;
                    if (name.Length >= 3 && !Keywords.Contains(name))
                        names.Add(name);
                }
            }
        }
        return names.ToList();
    }

    // Rekursiver, deterministisch sortierter Datei-Walk unter Auslassung von Vendor-/Build-Dirs
    // und zu großer/binärer Dateien.
    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            List<string> subdirs, files;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir)
                    .Where(d => !ExcludedDirs.Contains(Path.GetFileName(d)))
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList();  // Stack ⇒ absteigend rein = aufsteigend raus
                files = Directory.EnumerateFiles(dir)
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
            }
            catch { continue; }

            foreach (var sub in subdirs) stack.Push(sub);
            foreach (var f in files)
            {
                long len;
                try { len = new FileInfo(f).Length; } catch { continue; }
                if (len > 0 && len <= MaxFileBytes) yield return f;
            }
        }
    }

    // ---- Pfad-Sicherheit --------------------------------------------------
    // Verhindert Ausbruch aus dem Checkout (z. B. "../../etc/passwd" im Diff-Pfad).
    private static string? SafeResolve(string root, string relPath)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relPath));
        return full.StartsWith(rootFull, StringComparison.Ordinal) ? full : null;
    }
}
