using System.Text.Json;

namespace Naudit.Benchmark;

/// <summary>Nachweis, dass ein Review unter vollen Bedingungen lief. Naudit ist fail-open:
/// ein fehlgeschlagener Checkout, eine gescheiterte Profil-Destillation oder ein toter Analyzer
/// ergeben still ein schlechteres Review. Von außen sind zwei Spuren beobachtbar — ob der
/// Checkout überhaupt angefragt wurde (Dekorator) und was die Pipeline währenddessen als
/// Warning/Error geloggt hat. Beides zusammen fängt die fail-open-Pfade ab, die sich melden.
/// Auffällige Läufe werden am Ende berichtet und wiederholt, nicht importiert.</summary>
public sealed record ReviewDiagnostics(
    bool CheckoutRequested, IReadOnlyList<string> Warnings, double DurationSeconds, string? Error);

/// <summary>Ein Datensatz je PR: was Naudit gesagt hätte, plus unter welchen Bedingungen.</summary>
public sealed record BenchmarkRecord(string Url, CapturedReview Review, ReviewDiagnostics Diagnostics);

/// <summary>Ergebnisdatei und zugleich Wiederaufsetzpunkt. Nach jedem Review neu geschrieben —
/// der Lauf dauert Stunden, ein Abbruch darf nichts kosten.</summary>
public sealed class ResultStore
{
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly List<BenchmarkRecord> _records;

    public ResultStore(string path)
    {
        _path = path;
        _records = File.Exists(path)
            ? JsonSerializer.Deserialize<List<BenchmarkRecord>>(File.ReadAllText(path), JsonOpts) ?? []
            : [];
    }

    public IReadOnlyCollection<string> CompletedUrls => _records.Select(r => r.Url).ToHashSet();

    public IReadOnlyList<BenchmarkRecord> All() => _records;

    public void Append(BenchmarkRecord record)
    {
        _records.RemoveAll(r => r.Url == record.Url);
        _records.Add(record);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(_records, JsonOpts));
    }
}
