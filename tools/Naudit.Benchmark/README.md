# Naudit.Benchmark

Fährt Naudit über die 50 PRs des offline-Teils von `withmartian/code-review-benchmark` und
fängt die Review-Kommentare ab, statt sie zu posten.

> **Dieses Werkzeug schreibt nichts nach GitHub.** `PostReviewAsync` ist durch
> `CapturingGitPlatform` ersetzt; gelesen wird über die echte GitHub-Anbindung. Der Token für
> den Lauf sollte trotzdem read-only sein — zwei Schlösser sind besser als eines.

## Voraussetzungen

1. `claude` installiert und angemeldet, Token via `claude setup-token`.
2. Read-only GitHub-Token (öffentliche Repos genügen).
3. Klon des Benchmarks, z. B. nach `~/workspace/code-review-benchmark`.

## Umgebungsvariablen

| Variable | Bedeutung |
|---|---|
| `NAUDIT_BENCHMARK_REPO` | Pfad zum Benchmark-Klon (Pflicht) |
| `NAUDIT_BENCHMARK_OUTPUT` | Ergebnisdatei, Default `<repo>/offline/results/naudit-reviews.json` |
| `NAUDIT_BENCHMARK_LIMIT` | Anzahl Reviews in diesem Lauf (Default: alle; `1` für den Smoke-Test, `0` für reinen Preflight) |
| `NAUDIT_BENCHMARK_PAUSE_SECONDS` | Pause zwischen Reviews, Default 20 (Abo-Kontingent) |

Naudits eigene Konfiguration kommt wie gewohnt über `Naudit__*`-Variablen — siehe den
Implementierungsplan, Task 8.

## Ablauf

```bash
NAUDIT_BENCHMARK_LIMIT=0 dotnet run --project tools/Naudit.Benchmark   # Preflight
NAUDIT_BENCHMARK_LIMIT=1 dotnet run --project tools/Naudit.Benchmark   # Smoke-Test
dotnet run --project tools/Naudit.Benchmark                            # Vollauf
```

Der Lauf ist unterbrechbar: erledigte PRs stehen in der Ergebnisdatei und werden beim nächsten
Start übersprungen. Am Ende meldet das Werkzeug auffällige Reviews (Fehler, fehlender Checkout,
Warnungen aus der Pipeline) — die gehören wiederholt, **nicht** importiert, sonst zählt ein
stumm degradiertes Review als „nichts gefunden".

Import und Auswertung danach: `tools/benchmark/README` bzw. Task 9 des Plans.
