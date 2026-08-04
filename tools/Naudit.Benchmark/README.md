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

Die beiden Zahlen scheitern **laut**, wenn sie gesetzt, aber unlesbar oder negativ sind — ein
Tippfehler in `NAUDIT_BENCHMARK_LIMIT` liefe sonst still als Vollauf über alle 50 PRs durch.

Naudits eigene Konfiguration kommt wie gewohnt über `Naudit__*`-Variablen — siehe den
Implementierungsplan, Task 8.

## Ablauf

```bash
NAUDIT_BENCHMARK_LIMIT=0 dotnet run --project tools/Naudit.Benchmark   # Preflight
NAUDIT_BENCHMARK_LIMIT=1 dotnet run --project tools/Naudit.Benchmark   # Smoke-Test
dotnet run --project tools/Naudit.Benchmark                            # Vollauf
```

Der Lauf ist unterbrechbar: erledigte PRs stehen in der Ergebnisdatei und werden beim nächsten
Start übersprungen.

## Diagnose je Review

Naudits Pipeline ist fail-open: ein gescheiterter Checkout, eine gescheiterte Profil-Destillation
oder eine leer gebliebene Kontextsammlung ergeben still ein schwächeres Review — teils ohne dass
irgendjemand es loggt. Der Lauf hält deshalb je Review fest:

| Feld | Bedeutung |
|---|---|
| `checkoutRequested` / `checkoutFailed` | ob ein Checkout versucht wurde und ob er warf |
| `headRef` / `headSha` | welcher Ref und welcher **Commit** tatsächlich ausgecheckt war (die Klon-URL trägt das Token und wird nie festgehalten) |
| `contextInPrompt` / `guidelinesInPrompt` | ob der Prompt die Repo-Kontext- bzw. Architektur-Profil-Sektion trug |
| `inputTokens` / `outputTokens` | Token-Verbrauch aus `ChatResponse.Usage` |
| `changedFiles` | gesehene Dateien — bei 100 ist die Seitengrenze von `GetChangesAsync` erreicht und der PR womöglich gekürzt reviewt |
| `warnings` / `error` / `durationSeconds` | was die Pipeline geloggt hat, ein Abbruch, die Laufzeit |

Am Ende meldet das Werkzeug alle auffälligen Reviews. Die gehören **wiederholt, nicht importiert** —
sonst zählt ein stumm degradiertes Review als „nichts gefunden". `import_reviews.py` lehnt dieselben
Datensätze ab.

Getrennt davon steht ein Hinweis-Block für PRs mit voller Dateiseite: die sind kein
Wiederholungsgrund (ein erneuter Lauf sähe dasselbe), gehören aber als Grenze in die Arbeit.

Import und Auswertung danach: `tools/benchmark/import_reviews.py` bzw. Task 9 des Plans.
