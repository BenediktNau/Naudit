using Naudit.Infrastructure.Workspace;
using Xunit;

namespace Naudit.Tests;

/// <summary>Symlink-Ausbruch aus dem PR-Checkout: ein Link (Datei oder Elternordner) darf nie
/// gelesen werden — er könnte auf /proc/self/environ, die Naudit-DB oder .git/config zeigen.
/// Echte Symlinks ⇒ nur POSIX (auf Windows bräuchte das Anlegen Admin/Developer-Mode).</summary>
public class WorkspacePathsTests : IDisposable
{
    private static bool Posix => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private readonly string _root = Directory.CreateTempSubdirectory("naudit-ws-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("naudit-outside-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        try { Directory.Delete(_outside, recursive: true); } catch { }
    }

    private string Secret()
    {
        var p = Path.Combine(_outside, "secret.txt");
        File.WriteAllText(p, "TOP-SECRET");
        return p;
    }

    [Fact]
    public void Resolve_regularFile_returnsFullPath()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.cs"), "x");

        Assert.Equal(Path.Combine(_root, "src", "a.cs"), WorkspacePaths.Resolve(_root, "src/a.cs"));
    }

    [Fact]
    public void Resolve_dotDotEscape_returnsNull()
    {
        Assert.Null(WorkspacePaths.Resolve(_root, "../etc/passwd"));
        // Präfix-Trick: Geschwister-Ordner "<root>-evil" besteht keinen reinen StartsWith-Check.
        Directory.CreateDirectory(_root + "-evil");
        try
        {
            File.WriteAllText(Path.Combine(_root + "-evil", "x"), "x");
            Assert.Null(WorkspacePaths.Resolve(_root, "../" + Path.GetFileName(_root) + "-evil/x"));
        }
        finally { Directory.Delete(_root + "-evil", recursive: true); }
    }

    [Fact]
    public void Resolve_missingFileOrDirectory_returnsNull()
    {
        Directory.CreateDirectory(Path.Combine(_root, "dir"));

        Assert.Null(WorkspacePaths.Resolve(_root, "nope.cs"));
        Assert.Null(WorkspacePaths.Resolve(_root, "dir"));   // nur reguläre Dateien
    }

    [Fact]
    public void Resolve_fileSymlink_returnsNull()
    {
        if (!Posix) return;
        File.CreateSymbolicLink(Path.Combine(_root, "README.md"), Secret());

        Assert.Null(WorkspacePaths.Resolve(_root, "README.md"));
    }

    [Fact]
    public void Resolve_fileBelowSymlinkedDirectory_returnsNull()
    {
        if (!Posix) return;
        Secret();
        Directory.CreateSymbolicLink(Path.Combine(_root, "docs"), _outside);

        Assert.Null(WorkspacePaths.Resolve(_root, "docs/secret.txt"));
    }

    [Fact]
    public void Resolve_danglingSymlink_returnsNull()
    {
        if (!Posix) return;
        File.CreateSymbolicLink(Path.Combine(_root, "gone.md"), Path.Combine(_outside, "missing"));

        Assert.Null(WorkspacePaths.Resolve(_root, "gone.md"));
    }

    [Fact]
    public void EnumerateFiles_skipsSymlinkedFilesAndDirectories()
    {
        if (!Posix) return;
        File.WriteAllText(Path.Combine(_root, "ok.md"), "ok");
        File.CreateSymbolicLink(Path.Combine(_root, "link.md"), Secret());
        Directory.CreateSymbolicLink(Path.Combine(_root, "linkdir"), _outside);

        var files = WorkspacePaths.EnumerateFiles(_root).Select(Path.GetFileName).ToList();
        var dirs = WorkspacePaths.EnumerateDirectories(_root).ToList();

        Assert.Equal(["ok.md"], files);
        Assert.Empty(dirs);
    }

    [Fact]
    public void EnumerateFilesRecursive_doesNotDescendIntoSymlinkedDirectories()
    {
        if (!Posix) return;
        Directory.CreateDirectory(Path.Combine(_root, "docs", "sub"));
        File.WriteAllText(Path.Combine(_root, "docs", "sub", "a.md"), "a");
        File.WriteAllText(Path.Combine(_outside, "b.md"), "b");
        Directory.CreateSymbolicLink(Path.Combine(_root, "docs", "evil"), _outside);

        var files = WorkspacePaths.EnumerateFilesRecursive(Path.Combine(_root, "docs"), "*.md")
            .Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/')).ToList();

        Assert.Equal(["docs/sub/a.md"], files);
    }
}
