using Naudit.Core.Models;

namespace Naudit.Core.Review;

/// <summary>Parst Unified-Diffs zu den kommentierbaren Zeilen je Datei.</summary>
public static class DiffParser
{
    /// <summary>
    /// Liefert je Datei eine Map New-File-Zeilennummer -> alte Zeilennummer (null bei hinzugefügter Zeile).
    /// Nur hinzugefügte (+) und Kontext-Zeilen ( ) sind enthalten; gelöschte (-) Zeilen nicht.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<int, int?>> Parse(IReadOnlyList<CodeChange> changes)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<int, int?>>();
        foreach (var change in changes)
        {
            var map = new Dictionary<int, int?>();
            int oldLine = 0, newLine = 0;
            var inHunk = false;
            foreach (var raw in change.Diff.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("@@", StringComparison.Ordinal))
                {
                    var (oldStart, newStart) = ParseHunkHeader(line);
                    oldLine = oldStart - 1;
                    newLine = newStart - 1;
                    inHunk = true;
                    continue;
                }
                // Datei-Header (+++/---) stehen nur vor dem ersten Hunk; innerhalb eines Hunks
                // sind +/- echte Inhaltszeilen (z. B. entfernter SQL-Kommentar "-- x" -> "--- x").
                if (!inHunk && (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)))
                    continue;
                if (line.Length == 0)
                    continue;
                switch (line[0])
                {
                    case '+':
                        newLine++;
                        map[newLine] = null;
                        break;
                    case '-':
                        oldLine++;
                        break;
                    case ' ':
                        oldLine++;
                        newLine++;
                        map[newLine] = oldLine;
                        break;
                    // '\' (No newline at end of file) u. Ä. ignorieren
                }
            }
            result[change.FilePath] = map;
        }
        return result;
    }

    /// <summary>Liest aus "@@ -oldStart[,n] +newStart[,n] @@" die beiden Startzeilen.</summary>
    internal static (int OldStart, int NewStart) ParseHunkHeader(string header)
    {
        var minus = header.IndexOf('-');
        var plus = header.IndexOf('+');
        return (ReadNumber(header, minus + 1), ReadNumber(header, plus + 1));
    }

    private static int ReadNumber(string s, int start)
    {
        int i = start, val = 0;
        while (i < s.Length && char.IsDigit(s[i]))
        {
            val = val * 10 + (s[i] - '0');
            i++;
        }
        return val;
    }
}
