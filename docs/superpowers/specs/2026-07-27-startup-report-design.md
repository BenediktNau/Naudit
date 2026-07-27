# Design: Startup-Report — aktive Konfiguration beim Hochfahren loggen

*2026-07-27 · Projekt: Naudit*

## Ziel

Naudit loggt beim Start heute nichts über seine eigene Konfiguration — nach einem
Deploy sieht man im Coolify-Log nur die ASP.NET-Standardzeilen. Ob die Instanz
gegen GitHub oder GitLab läuft, welcher AI-Provider aktiv ist und vor allem **ob
SAST und DAST greifen**, lässt sich nur durch Nachsehen in den Settings oder in
den Env-Variablen beantworten. Das ist besonders unangenehm, weil ein Teil dieser
Schalter DB-verwaltet ist und sich über die WebUI ändert: nach einem
Settings-Restart ist nicht sichtbar, was der Host nun tatsächlich geladen hat.

Ziel: ein kompakter, kuratierter Block im Log direkt beim Hochfahren, der den
effektiven Zustand zeigt — plus Warnzeilen für Konfigurationen, die zwar
gültig sind, aber wirkungslos bleiben.

## Entscheidungen

- **Nur Log, keine WebUI.** Der Block beantwortet „womit ist der Prozess
  gestartet". Ein Laufzeit-Panel wäre ein eigenes Feature (eigener Endpoint,
  Sichtbarkeitsregeln, React-Komponente) und ist hier bewusst nicht drin.
- **Kuratierte Zeilen statt Katalog-Dump.** Nicht jeder `SettingsCatalog`-Schlüssel,
  sondern handverlesen die Schalter, die das Verhalten sichtbar ändern. Ein
  40-Zeilen-Dump wird nicht gelesen.
- **Aus `IConfiguration`, nicht aus dem DI-Container.** `AddNauditInfrastructure`
  läuft im Setup- und im Recovery-Modus **nicht** — ein Report, der `SastOptions`
  aus dem Container zöge, wäre ausgerechnet im Fehlerfall leer. Die Bindung aus der
  Config funktioniert in allen drei Modi identisch.
- **Keine Secrets, auch nicht maskiert.** Ausgegeben werden nur Enums, Bools,
  Zahlen und Analyzer-/Projektnamen. `Naudit:Ai:Endpoint` bleibt bewusst draußen:
  bei manchen OpenAI-kompatiblen Diensten steckt der Key im Pfad.
- **Fail-safe.** Der gesamte Aufruf liegt in `try/catch`; ein Fehler im Report
  wird als `LogWarning` quittiert und hindert den Host nie am Start. Ein
  Diagnose-Feature darf das Primärverhalten nicht gefährden — dieselbe
  Philosophie wie beim Audit-Sink.
- **Der Block erscheint bei jedem Neustart.** Die Hostschleife in `Program.cs`
  läuft nach einem Settings-Restart erneut durch `BuildApp`; der Block zeigt dann
  die neuen Werte. Das ist die eigentliche Verifikation einer Settings-Änderung.

## Komponente

Neue Datei `src/Naudit.Web/StartupReport.cs`:

```csharp
public static class StartupReport
{
    public static IReadOnlyList<string> BuildLines(
        IConfiguration config, SetupStatusResult setup, string? recoveryError);

    public static IReadOnlyList<string> BuildWarnings(IConfiguration config);

    public static void Log(
        ILogger logger, IConfiguration config, SetupStatusResult setup, string? recoveryError);
}
```

`BuildLines`/`BuildWarnings` sind reine Funktionen — Config rein, Strings raus.
Das ist die gesamte Testfläche; `Log` ist nur die Ausgabeschleife
(`LogInformation` je Blockzeile, `LogWarning` je Warnzeile) plus das `try/catch`.

**Aufrufstelle:** `src/Naudit.Web/Program.cs` unmittelbar nach
`var app = builder.Build();` (heute Zeile 229). Dort existiert `app.Logger`, und
`setup` sowie `configError` sind bereits lokale Variablen in `BuildApp`. Damit
steht der Block **vor** den Kestrel-/Hosting-Zeilen im Log.

## Der Block

```
════════════ Naudit v0.4.2 ════════════
Modus:      Review aktiv
Plattform:  GitHub · Auth: App · PostVerdict: aus
AI:         Anthropic · claude-opus-5 · Routing: Single · Sandbox: None · MCP: aus · Logging: aus
SAST:       AN · opengrep, trivy, osv-scanner
DAST:       AN · Allowlist: acme/web, acme/api
Prompt:     Kontext AN · Memory AN (max 50) · Guidelines AN · Redaction AN
Review:     Gate ab High/Medium · MaxRoundtrips 3 · Resolution AN
Zugang:     AccessGate Open · DB Sqlite
═══════════════════════════════════════
```

Feldherkunft:

| Zeile | Quelle |
| --- | --- |
| Modus | `SetupStatusResult.SetupRequired`, `configError` |
| Plattform | `Naudit:Git:Platform`, `Naudit:GitHub:Auth`, `Naudit:{GitHub,GitLab}:PostVerdict` |
| AI | `Naudit:Ai:{Provider,Model,SessionRouting,SessionSandbox}`, `Naudit:Review:Mcp:Enabled`, `Naudit:Ai:Logging:Enabled` |
| SAST | `Naudit:Sast:Enabled`, `Naudit:Sast:Analyzers` (über `SastOptions.ResolveAnalyzers`) |
| DAST | `Naudit:Review:Dast:Enabled`, `Naudit:Review:Dast:Projects` |
| Prompt | `Naudit:Review:{Context,Memory,Guidelines}:Enabled`, `Naudit:Review:Memory:MaxEntries`, `Naudit:Redaction:Enabled` |
| Review | `Naudit:Review:Gate:{MinSeverity,MinConfidence}`, `Naudit:Review:MaxRoundtrips`, `Naudit:Review:Resolution:Enabled` |
| Zugang | `Naudit:AccessGate:Mode`, `Naudit:Db:Provider` |

**Setup-Modus:** `Modus: SETUP — Wizard aktiv, Webhooks nicht gemappt`, gefolgt
von den `MissingKeys` aus `SetupStatusResult`.
**Recovery-Modus:** `Modus: RECOVERY` plus die Fehlermeldung aus `configError`.
In beiden Fällen bleiben die übrigen Zeilen stehen — sichtbar zu machen, was
bereits konfiguriert ist, ist gerade dort der Punkt.

## Warnzeilen

Nach dem Block, je eine `LogWarning`:

| Bedingung | Meldung |
| --- | --- |
| `Dast.Enabled` und `Dast.Projects` leer | DAST aktiviert, aber Allowlist leer — kein Projekt wird dynamisch getestet |
| `Sast.Enabled` und `Sast.Analyzers` leer | Kein Analyzer konfiguriert — Default `opengrep, trivy` greift |
| `SessionSandbox=Docker` und `SessionRouting=Single` | Sandbox ohne Wirkung — greift nur bei Author/RoundRobin |
| `MaxRoundtrips <= 0` | Roundtrip-Limit deaktiviert — jeder Push löst ein Review aus |

## Begleitende Aufräumaktion: `SastOptions.ResolveAnalyzers`

`DependencyInjection.cs:300` setzt heute den Default `opengrep, trivy`, wenn
`Naudit:Sast:Analyzers` leer ist. Läge diese Logik nur dort, meldete der Report
„keine Analyzer", während zwei laufen. Der Fallback wandert deshalb als statische
Methode nach `SastOptions` — analog zum bereits vorhandenen
`SastOptions.ResolveOpengrepRules` —, und DI wie Report rufen dieselbe Methode.
Kein Verhaltenswechsel, eine Quelle statt zwei.

## Versions-Stamping

Die Assembly wird derzeit nirgends versioniert: weder `Dockerfile` noch
`release.yml` geben `/p:Version` mit, die berechnete SemVer landet nur im Image-Tag
und im Git-Tag. Ohne Änderung zeigte die Kopfzeile stumpf `1.0.0`.

- `Dockerfile`: `ARG VERSION=0.0.0` in der Build-Stage, durchgereicht als
  `dotnet publish … /p:Version=$VERSION`.
- `release.yml`: `build-args: VERSION=…` an `docker/build-push-action`, gespeist
  aus `steps.version.outputs.version` mit gestripptem führendem `v`; derselbe
  Wert als `/p:Version` an die self-contained-Binary-Publishes.
- `StartupReport` liest `AssemblyInformationalVersionAttribute`. Ohne Stamping
  (lokaler `dotnet run`) erscheint `v1.0.0 (dev)`.

## Tests

`tests/Naudit.Tests/StartupReportTests.cs` — reine Unit-Tests gegen `BuildLines`
und `BuildWarnings` mit einem In-Memory-`ConfigurationBuilder`, kein Host, kein
`WebApplicationFactory`:

- GitHub mit `Auth=App` vs. GitLab — richtige Plattformzeile, GitHub-spezifische
  Felder fehlen im GitLab-Fall.
- SAST an mit expliziter Analyzer-Liste; SAST an mit leerer Liste (Default-Fallback
  erscheint in der Zeile **und** erzeugt die Warnung); SAST aus.
- DAST an mit Allowlist; DAST an ohne Allowlist (Warnung); DAST aus.
- Setup-Modus: Modus-Zeile plus die fehlenden Schlüssel.
- Recovery-Modus: Modus-Zeile plus Fehlermeldung.
- Sandbox=Docker bei Routing=Single erzeugt die Warnung, bei Routing=Author nicht.
- Secret-Test: mit gesetzten Tokens/API-Keys kommt keiner dieser Werte in einer
  der Zeilen vor — iteriert über die `IsSecret`-Einträge des `SettingsCatalog`.

## Nicht enthalten (bewusst)

- Kein WebUI-Panel, kein neuer Endpoint.
- Keine Herkunftsanzeige (env/DB/appsettings) je Zeile — der `EnvOverrides`-Mechanismus
  existiert dafür bereits in der Settings-API.
- Keine Ausgabe von `ProjectTokens`, `Ui:Admins` oder MCP-Serverlisten.
