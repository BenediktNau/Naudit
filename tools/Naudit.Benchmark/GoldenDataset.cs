using System.Text.Json;
using System.Text.RegularExpressions;

namespace Naudit.Benchmark;

/// <summary>Ein Eintrag des Benchmark-Datensatzes: der zu reviewende PR.</summary>
public sealed record GoldenEntry(string Url, string PrTitle, string ProjectId, int Number);

/// <summary>Liest golden_comments/*.json. Maßgeblich ist das Feld "url" — 35 der 50 Einträge
/// zeigen auf den Upstream-PR, 15 auf vorbereitete PRs in der Org ai-code-review-evaluation
/// (für die es gar keinen Upstream-PR gibt). Der Originalweg klont ebenfalls, was in "url" steht;
/// nur so reviewt Naudit dieselbe Vorlage wie die Vergleichstools.</summary>
public static class GoldenDataset
{
    private static readonly Regex PullUrl = new(
        @"^https://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/pull/(?<number>\d+)/?$",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static GoldenEntry Parse(string url, string prTitle)
    {
        var m = PullUrl.Match(url ?? string.Empty);
        if (!m.Success)
            throw new InvalidOperationException(
                $"Keine auswertbare GitHub-PR-URL: '{url}'. Erwartet: https://github.com/<owner>/<repo>/pull/<nummer>");

        return new GoldenEntry(url!, prTitle,
            $"{m.Groups["owner"].Value}/{m.Groups["repo"].Value}",
            int.Parse(m.Groups["number"].Value));
    }

    public static IReadOnlyList<GoldenEntry> Load(string goldenCommentsDir)
    {
        var entries = new List<GoldenEntry>();
        foreach (var file in Directory.EnumerateFiles(goldenCommentsDir, "*.json").OrderBy(f => f))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                var title = item.TryGetProperty("pr_title", out var t) ? t.GetString() : null;
                entries.Add(Parse(url ?? string.Empty, title ?? string.Empty));
            }
        }
        return entries;
    }
}
