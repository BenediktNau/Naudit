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
sonst zählt ein stumm degradiertes Review als „nichts gefunden“. `import_reviews.py` lehnt dieselben
Datensätze ab, und der Runner selbst nimmt sie beim nächsten Start automatisch wieder auf.

Getrennt davon steht ein Hinweis-Block für PRs mit voller Dateiseite: die sind kein
Wiederholungsgrund (ein erneuter Lauf sähe dasselbe), gehören aber als Grenze in die Arbeit.

Import und Auswertung danach: `tools/benchmark/import_reviews.py` bzw. Task 9 des Plans.

## Ergebnis des Laufs vom 2026-08-14

50/50 PRs, 74 min, 371 Inline-Kommentare (⌀ 7,4 je PR), ein Review wegen einer Pipeline-Warnung
wiederholt, ein PR an der Seitengrenze von 100 Dateien. Bewertet über alle drei veröffentlichten
Judges, je 0 Fehler:

| Judge | Precision | Recall | F1 | Rang F1 | Rang Recall |
|---|---|---|---|---|---|
| Sonnet 4.5 | 20,4 % | 69,3 % | 31,5 % | 28 / 41 | 2 / 41 |
| Opus 4.5 | 22,5 % | 67,9 % | 33,8 % | 29 / 42 | 3 / 42 |
| GPT-5.2 | 19,5 % | 67,9 % | 30,3 % | 27 / 42 | 2 / 42 |

Naudit ist damit ein **High-Recall-/Low-Precision-Reviewer**: es findet fast am meisten im Feld
(nur `cubic-dev` liegt höher) und erzeugt dabei mehr als doppelt so viele Kommentare wie der
Feld-Schnitt (478 Kandidaten gegen 213). Der Befund ist über alle drei Judges stabil, auch über
den herstellerfremden. Die gemessene Precision ist eine Untergrenze — ein echter Fund, den kein
Annotator notierte, zählt hier als Fehlalarm.

Einordnung, Grenzen und die vier Abweichungen vom Originalverfahren stehen in
`docs/superpowers/specs/2026-08-04-code-review-benchmark-design.md`.

Wichtig für einen Wiederholungslauf: `Naudit__Review__Resolution__RenderHint=false` setzen, sonst
hängt an jedem erfassten Kommentar der `@naudit fp/ok`-Hinweis und wird als Inhalt mitbewertet.

### Falle beim Judge-Modell

`uv run` lädt `offline/.env` selbst nach. Steht dort ein `MARTIAN_MODEL_ENDPOINT`, füllt es jede
im aufrufenden Skript ge-`unset`-ete Variable wieder auf — und der Endpunkt schlägt den
Verzeichnisnamen. Am 14.08. lief ein als GPT-5.2 gemeinter Durchgang deshalb in Wahrheit auf
Sonnet 4.5 und schrieb trotzdem nach `results/openai_gpt-5.2/`. Sichtbar war das nur an der Zeile
`Judge model:` im Log und daran, dass die Kostenaufstellung des Anbieters kein GPT-Modell
auswies. **Modell und Endpunkt immer explizit exportieren, nie in der `.env` stehen lassen, und
nach jedem Lauf die `Judge model:`-Zeile prüfen.**

Der Fehllauf ist als Messung brauchbar: derselbe Judge über dieselben Eingaben ergab 20,4 → 19,8 %
Precision, 69,3 → 67,9 % Recall, 31,5 → 30,6 % F1, Rang 28 → 27. **Unterschiede unter einem
Prozentpunkt sind Rauschen der Bewertungspipeline, kein Signal.**
