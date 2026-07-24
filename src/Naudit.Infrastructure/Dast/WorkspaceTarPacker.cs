using System.Formats.Tar;

namespace Naudit.Infrastructure.Dast;

/// <summary>Packt den Checkout als Tar-Strom für den Docker-Build-Kontext. Nötig, weil die Engine
/// den Kontext aus dem Request-Body liest — der Daemon sieht Naudits Dateisystem nicht (Sibling-
/// Container). `.git` bleibt draußen (reiner Ballast, oft der größte Anteil), und ab MaxContextMb
/// bricht der Packer ab, statt hunderte MB durch den Socket zu schieben.</summary>
public static class WorkspaceTarPacker
{
    public static async Task<Stream?> PackAsync(string rootPath, int maxContextMb, CancellationToken ct = default)
    {
        var limit = (long)maxContextMb * 1024 * 1024;
        var buffer = new MemoryStream();
        var overCap = false;
        await using (var tar = new TarWriter(buffer, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                if (relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal))
                    continue;
                if (buffer.Length + SafeLength(file) > limit)
                {
                    // Erst raus aus dem using, DANN verwerfen: der TarWriter schreibt beim
                    // Schließen noch End-of-Archive-Blöcke und darf keinen toten Stream sehen.
                    overCap = true;
                    break;
                }
                // Sonderdateien (tote Symlinks, Sockets, Rechte) dürfen den Kontext nicht kippen.
                try { await tar.WriteEntryAsync(file, relative, ct); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        if (overCap)
        {
            await buffer.DisposeAsync();
            return null;
        }
        buffer.Position = 0;
        return buffer;
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch (IOException) { return 0; }
    }
}
