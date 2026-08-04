using System.Text.Json;

namespace Naudit.Benchmark;

/// <summary>Nachweis, dass ein Review unter vollen Bedingungen lief. Naudit ist fail-open:
/// ein fehlgeschlagener Checkout, eine gescheiterte Profil-Destillation oder ein toter Analyzer
/// ergeben still ein schlechteres Review. Beobachtbar ist das über drei Quellen: der Checkout
/// (angefragt/gescheitert, IGitPlatform-Dekorator), der tatsächliche Prompt-Inhalt samt Tokens
/// (IChatClient-Dekorator) und was die Pipeline währenddessen als Warning/Error geloggt hat.
/// Auffällige Läufe werden am Ende berichtet und wiederholt, nicht importiert.</summary>
/// <param name="CheckoutRequested">Wurde ein Checkout überhaupt versucht? false ⇒ Fehlkonfiguration
/// (Kontext aus), das Review lief diff-only.</param>
/// <param name="CheckoutFailed">Warf der Checkout? true ⇒ diff-only ohne Repo-Kontext und ohne
/// frisches Architektur-Profil — geloggt wird das nirgends.</param>
/// <param name="HeadRef">Der Ref, den Naudit ausgecheckt hat. Die Klon-URL wird NICHT festgehalten
/// (sie trägt das Token).</param>
/// <param name="ContextInPrompt">Trug der Review-Prompt die Repo-Kontext-Sektion?</param>
/// <param name="GuidelinesInPrompt">Trug der Review-Prompt das Architektur-Profil?</param>
/// <param name="InputTokens">Prompt-Tokens aus ChatResponse.Usage (null ⇒ Provider meldet keins).</param>
/// <param name="OutputTokens">Antwort-Tokens aus ChatResponse.Usage.</param>
public sealed record ReviewDiagnostics(
    bool CheckoutRequested, bool CheckoutFailed, string? HeadRef,
    bool ContextInPrompt, bool GuidelinesInPrompt, long? InputTokens, long? OutputTokens,
    IReadOnlyList<string> Warnings, double DurationSeconds, string? Error);

/// <summary>Ein Datensatz je PR: was Naudit gesagt hätte, plus unter welchen Bedingungen.</summary>
public sealed record BenchmarkRecord(string Url, CapturedReview Review, ReviewDiagnostics Diagnostics);

/// <summary>Ergebnisdatei und zugleich Wiederaufsetzpunkt. Nach jedem Review neu geschrieben —
/// der Lauf dauert Stunden, ein Abbruch darf nichts kosten. Schreibt atomar (via Temp-Datei)
/// und überlebt korrupte Eingaben, um nicht alle bisherigen Ergebnisse zu verlieren.</summary>
public sealed class ResultStore
{
    private static readonly JsonSerializerOptions JsonOpts =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly List<BenchmarkRecord> _records;

    public ResultStore(string path)
    {
        _path = path;
        _records = [];
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<List<BenchmarkRecord>>(json, JsonOpts);
                if (loaded != null)
                {
                    _records.AddRange(loaded);
                }
            }
            catch (JsonException ex)
            {
                // Abgeschnittene oder beschädigte Datei (z.B. nach Abbruch während Write).
                // Zur Diagnose beiseitelegen, mit leerer Liste weitermachen.
                var corruptPath = $"{path}.corrupt";
                try
                {
                    File.Move(path, corruptPath, overwrite: true);
                    Console.WriteLine($"[ResultStore] Korrupte Datei {path} nach {corruptPath} verschoben. " +
                        $"Lauf wird mit leerer Liste fortgesetzt. Fehler: {ex.Message}");
                }
                catch
                {
                    Console.WriteLine($"[ResultStore] Datei {path} ist korrupt (JsonException: {ex.Message}), " +
                        $"konnte aber nicht verschoben werden. Lauf wird mit leerer Liste fortgesetzt.");
                }
            }
        }
    }

    public IReadOnlyCollection<string> CompletedUrls => _records.Select(r => r.Url).ToHashSet();

    public IReadOnlyList<BenchmarkRecord> All() => _records;

    public void Append(BenchmarkRecord record)
    {
        // Ersetzen an Ort und Stelle: Reihenfolge bleibt erhalten.
        var index = _records.FindIndex(r => r.Url == record.Url);
        if (index >= 0)
        {
            _records[index] = record;
        }
        else
        {
            _records.Add(record);
        }

        // Atomar schreiben: in Temp-Datei, dann per Move ersetzen.
        var dirPath = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dirPath))
        {
            Directory.CreateDirectory(dirPath);
        }

        var tempPath = $"{_path}.tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_records, JsonOpts));
        File.Move(tempPath, _path, overwrite: true);
    }
}
