using System.Formats.Tar;
using Naudit.Infrastructure.Dast;
using Xunit;

namespace Naudit.Tests;

public class WorkspaceTarPackerTests
{
    private static string NewCheckout(params (string Path, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), $"naudit-tar-{Guid.NewGuid():N}");
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return root;
    }

    private static async Task<List<string>> EntryNamesAsync(Stream tar)
    {
        var names = new List<string>();
        await using var reader = new TarReader(tar);
        while (await reader.GetNextEntryAsync() is { } entry)
            names.Add(entry.Name);
        return names;
    }

    [Fact]
    public async Task Pack_containsRepoFiles_relativeToRoot_andSkipsGitDirectory()
    {
        var root = NewCheckout(
            ("Dockerfile", "FROM scratch"),
            (Path.Combine("src", "app.cs"), "class A {}"),
            (Path.Combine(".git", "config"), "[core]"));

        var tar = await WorkspaceTarPacker.PackAsync(root, maxContextMb: 10);

        Assert.NotNull(tar);
        var names = await EntryNamesAsync(tar!);
        Assert.Contains("Dockerfile", names);
        Assert.Contains("src/app.cs", names);
        Assert.DoesNotContain(names, n => n.StartsWith(".git", StringComparison.Ordinal));
    }

    /// <summary>Der Kontext wandert komplett über den Socket in den Daemon — ein Riesen-Checkout
    /// wird abgelehnt (null) statt hunderte MB zu schieben.</summary>
    [Fact]
    public async Task Pack_overSizeCap_returnsNull()
    {
        var root = NewCheckout(("big.bin", new string('x', 2 * 1024 * 1024)));

        Assert.Null(await WorkspaceTarPacker.PackAsync(root, maxContextMb: 1));
    }

    /// <summary>Realistischer Über-Cap-Fall: die erste Datei passt noch, die zweite kippt die
    /// Summe — muss null liefern statt zu werfen (der TarWriter schreibt beim Schließen noch
    /// End-of-Archive-Blöcke in den Buffer).</summary>
    [Fact]
    public async Task Pack_overSizeCap_afterFirstEntry_returnsNull()
    {
        var root = NewCheckout(
            ("a.bin", new string('x', 700 * 1024)),
            ("b.bin", new string('y', 700 * 1024)));

        Assert.Null(await WorkspaceTarPacker.PackAsync(root, maxContextMb: 1));
    }
}
