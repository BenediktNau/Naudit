using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Infrastructure.Context;
using Xunit;

namespace Naudit.Tests;

public class WorkspaceContextCollectorTests
{
    // ---- Fixtures ---------------------------------------------------------
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "naudit-ctx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content.Replace("\r\n", "\n"));
    }

    private sealed class TestWorkspace(string root) : IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(RootPath, recursive: true); } catch { /* best effort */ }
            return ValueTask.CompletedTask;
        }
    }

    // ---- Tests ------------------------------------------------------------
    [Fact]
    public async Task Collect_smallChangedFile_includesWholeFile()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        WriteFile(root, "src/Foo.cs", "class Foo\n{\n    void A() { return; }\n}\n");
        var changes = new[] { new CodeChange("src/Foo.cs", "@@ -3,0 +3,1 @@\n+    void A() { return; }") };
        var sut = new WorkspaceContextCollector(new ReviewContextOptions());

        var ctx = await sut.CollectAsync(ws, changes);

        var env = Assert.Single(ctx.Environments);
        Assert.Equal("src/Foo.cs", env.FilePath);
        Assert.True(env.IsFullFile);
        Assert.Equal(1, env.StartLine);
        Assert.Contains("class Foo", env.Content);
        Assert.Contains("void A()", env.Content);
    }

    [Fact]
    public async Task Collect_largeChangedFile_extractsEnclosingBlock_notWholeFile()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        // 11 Zeilen, zwei Methoden. Anker = New-File-Zeile 6 (DoThing im Body von A()).
        WriteFile(root, "src/Foo.cs",
            "namespace N;\n" +      // 1
            "class C\n" +           // 2
            "{\n" +                 // 3
            "    void A()\n" +      // 4
            "    {\n" +             // 5
            "        DoThing();\n" +// 6  <- Anker
            "    }\n" +             // 7
            "    void B()\n" +      // 8
            "    {\n" +             // 9
            "    }\n" +             // 10
            "}\n");                 // 11
        var changes = new[] { new CodeChange("src/Foo.cs", "@@ -6,0 +6,1 @@\n+        DoThing();") };
        // FullFileMaxLines klein ⇒ Block-Modus; BlockPadLines 0 ⇒ reine Einrückungs-Heuristik.
        var opts = new ReviewContextOptions { FullFileMaxLines = 5, BlockPadLines = 0 };
        var sut = new WorkspaceContextCollector(opts);

        var ctx = await sut.CollectAsync(ws, changes);

        var env = Assert.Single(ctx.Environments);
        Assert.False(env.IsFullFile);
        Assert.Equal(4, env.StartLine);              // Blockkopf "void A()"
        Assert.Contains("void A()", env.Content);
        Assert.Contains("DoThing();", env.Content);
        Assert.DoesNotContain("void B()", env.Content);   // Nachbar-Block bleibt draußen
    }

    [Fact]
    public async Task Collect_deletedFile_missingInWorkspace_isSkipped()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        // Datei existiert NICHT im Checkout (gelöscht) -> kein Environment, kein Crash.
        var changes = new[] { new CodeChange("gone.cs", "@@ -1,1 +0,0 @@\n-was here") };
        var sut = new WorkspaceContextCollector(new ReviewContextOptions());

        var ctx = await sut.CollectAsync(ws, changes);

        Assert.Empty(ctx.Environments);
    }
}
