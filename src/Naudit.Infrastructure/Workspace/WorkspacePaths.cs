namespace Naudit.Infrastructure.Workspace;

/// <summary>Sicherer Zugriff auf den PR-Checkout (Fremd-Content!). Ein reiner Pfad-String-Vergleich
/// reicht nicht: File.Exists/ReadAllText folgen Symlinks, ein PR könnte README.md, eine geänderte
/// Datei oder docs/x.md auf /proc/self/environ, die Naudit-DB oder .git/config zeigen lassen und so
/// deren Inhalt in den LLM-Prompt schleusen. Deshalb gilt für jeden Leser des Checkouts:
/// keine Verknüpfung — weder die Datei selbst noch ein Elternordner unterhalb des Roots.
/// (Zweite Verteidigungslinie; der Checkout selbst läuft bereits mit core.symlinks=false.)</summary>
public static class WorkspacePaths
{
    /// <summary>Absoluter Pfad einer existierenden, regulären Datei unter <paramref name="root"/> —
    /// oder null, wenn der Pfad aus dem Root ausbricht ("../"), nicht existiert, kein File ist oder
    /// er selbst bzw. ein Ordner auf dem Weg dorthin ein Symlink/Reparse-Point ist.</summary>
    public static string? Resolve(string root, string relPath)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string full;
        try { full = Path.GetFullPath(Path.Combine(rootFull, relPath)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return IsSafeFile(rootFull, full) ? full : null;
    }

    /// <summary>Prüft einen bereits absoluten Pfad (z. B. aus einer Enumeration): liegt unter dem
    /// Root (Separator-Grenze), ist eine reguläre Datei, und keine Komponente ist eine Verknüpfung.</summary>
    public static bool IsSafeFile(string root, string fullPath)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        // Separator-Grenze erzwingen: sonst bestünde ein Geschwister mit gleichem Präfix
        // (z. B. "…/ws" vs. "…/ws-evil") den Prefix-Check.
        var rootWithSep = rootFull + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSep, StringComparison.Ordinal))
            return false;

        try
        {
            var file = new FileInfo(fullPath);
            if (IsLink(file) || !file.Exists)   // dangling Link: Exists=false, aber LinkTarget gesetzt
                return false;
            // Jeden Elternordner bis (exklusive) Root prüfen — der Root selbst darf unter einem
            // Link liegen (z. B. /tmp → /private/tmp auf macOS), das ist Naudits eigener Pfad.
            for (var dir = file.Directory; dir is not null; dir = dir.Parent)
            {
                var dirFull = Path.TrimEndingDirectorySeparator(dir.FullName);
                if (dirFull.Equals(rootFull, StringComparison.Ordinal))
                    return true;
                if (IsLink(dir))
                    return false;
            }
            return false;   // Root nie erreicht ⇒ nicht darunter
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Symlink (Unix) oder Reparse-Point (Windows: Symlink/Junction).</summary>
    public static bool IsLink(FileSystemInfo info)
        => info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));

    /// <summary>Unterordner von <paramref name="dir"/> ohne Verknüpfungen (nicht rekursiv).</summary>
    public static IEnumerable<string> EnumerateDirectories(string dir)
        => new DirectoryInfo(dir).EnumerateDirectories().Where(d => !IsLink(d)).Select(d => d.FullName);

    /// <summary>Reguläre Dateien in <paramref name="dir"/> ohne Verknüpfungen (nicht rekursiv).</summary>
    public static IEnumerable<string> EnumerateFiles(string dir, string pattern = "*")
        => new DirectoryInfo(dir).EnumerateFiles(pattern).Where(f => !IsLink(f)).Select(f => f.FullName);

    /// <summary>Rekursiver Walk, der nie in verlinkte Ordner absteigt und verlinkte Dateien auslässt;
    /// Ergebnis ordinal sortiert (deterministisch). Unlesbare Ordner werden übersprungen.</summary>
    public static IEnumerable<string> EnumerateFilesRecursive(string dir, string pattern)
    {
        var result = new List<string>();
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            try
            {
                result.AddRange(EnumerateFiles(current, pattern));
                foreach (var sub in EnumerateDirectories(current))
                    stack.Push(sub);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* überspringen */ }
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
