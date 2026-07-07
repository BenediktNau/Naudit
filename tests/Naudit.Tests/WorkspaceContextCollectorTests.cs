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

    [Fact]
    public async Task Collect_findsCallSites_ofNewlyDeclaredSymbol_excludingDeclaringFile()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        WriteFile(root, "service.py", "def process_order(order):\n    return order\n");
        WriteFile(root, "caller.py", "def main():\n    result = process_order(o)\n    return result\n");
        // Änderung deklariert process_order in service.py.
        var changes = new[] { new CodeChange("service.py", "@@ -1,0 +1,1 @@\n+def process_order(order):") };
        var sut = new WorkspaceContextCollector(new ReviewContextOptions());

        var ctx = await sut.CollectAsync(ws, changes);

        var usage = Assert.Single(ctx.Usages);
        Assert.Equal("process_order", usage.Symbol);
        Assert.Equal("caller.py", usage.FilePath);       // Deklarationsdatei ausgeschlossen
        Assert.Equal(2, usage.Line);
        Assert.Contains("process_order(o)", usage.Snippet);
    }

    [Fact]
    public async Task Collect_capsUsagesPerSymbol()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        WriteFile(root, "svc.py", "def widget(x):\n    return x\n");
        // Fünf Aufrufe, MaxUsagesPerSymbol=2 ⇒ höchstens 2 Fundstellen.
        WriteFile(root, "a.py", "widget(1)\nwidget(2)\nwidget(3)\nwidget(4)\nwidget(5)\n");
        var changes = new[] { new CodeChange("svc.py", "@@ -1,0 +1,1 @@\n+def widget(x):") };
        var sut = new WorkspaceContextCollector(new ReviewContextOptions { MaxUsagesPerSymbol = 2 });

        var ctx = await sut.CollectAsync(ws, changes);

        Assert.Equal(2, ctx.Usages.Count);
    }

    [Fact]
    public async Task Collect_ignoresVendorDirs()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        WriteFile(root, "svc.py", "def gadget(x):\n    return x\n");
        WriteFile(root, "node_modules/dep.py", "gadget(99)\n");   // in Vendor-Dir -> ignoriert
        var changes = new[] { new CodeChange("svc.py", "@@ -1,0 +1,1 @@\n+def gadget(x):") };
        var sut = new WorkspaceContextCollector(new ReviewContextOptions());

        var ctx = await sut.CollectAsync(ws, changes);

        Assert.Empty(ctx.Usages);
    }

    [Fact]
    public async Task Collect_overview_hasTree_andReadmeHead()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        WriteFile(root, "README.md", "# MyProject\nDoes the thing.\nSecond line.\n");
        WriteFile(root, "src/App.cs", "class App { }\n");
        var changes = new[] { new CodeChange("src/App.cs", "@@ -1,0 +1,1 @@\n+class App { }") };
        var sut = new WorkspaceContextCollector(new ReviewContextOptions { ReadmeMaxLines = 2 });

        var ctx = await sut.CollectAsync(ws, changes);

        Assert.NotNull(ctx.Overview);
        Assert.Contains("src/", ctx.Overview!);            // Verzeichnisbaum
        Assert.Contains("App.cs", ctx.Overview!);
        Assert.Contains("# MyProject", ctx.Overview!);     // README-Kopf
        Assert.DoesNotContain("Second line.", ctx.Overview!);  // durch ReadmeMaxLines gedeckelt
    }

    [Fact]
    public async Task Collect_budget_truncatesAndMarks()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        WriteFile(root, "big.txt", new string('x', 200) + "\n");
        var changes = new[] { new CodeChange("big.txt", "@@ -1,0 +1,1 @@\n+" + new string('x', 200)) };
        var sut = new WorkspaceContextCollector(new ReviewContextOptions { MaxChars = 50 });

        var ctx = await sut.CollectAsync(ws, changes);

        var env = Assert.Single(ctx.Environments);
        Assert.Contains("[truncated by budget]", env.Content);
        Assert.True(env.Content.Length <= 50, $"content length {env.Content.Length} exceeds budget 50");
        Assert.Null(ctx.Overview);                          // Budget nach Umgebung erschöpft
    }
}
