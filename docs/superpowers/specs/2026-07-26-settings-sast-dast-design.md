# SAST/DAST in den Settings — Design

**Datum:** 2026-07-26
**Status:** approved

## Problem

Ob SAST läuft und welche Analyzer benutzt werden, ist heute **nur per Umgebungsvariable**
einstellbar: `Naudit:Sast:Enabled` und `Naudit:Sast:Analyzers` stehen nicht im
`SettingsCatalog`. Für DAST ist `Naudit:Review:Dast:Enabled` zwar DB-verwaltet (und damit in
der Raw-keys-Liste editierbar), die Projekt-Allowlist `Naudit:Review:Dast:Projects` aber nicht.
Ein Admin kann die Sicherheitswerkzeuge also nicht ohne Container-Redeploy umschalten.

Der Grund für die Lücke ist strukturell: **listenförmige Keys passen nicht ins heutige
Settings-Modell.** `SettingsService` schreibt eine Zeile pro Key, `DbSettingsLoader` liefert ein
flaches `Dictionary<string,string?>`, und Config-Binding auf `List<string>` liest ausschließlich
indizierte Kind-Keys (`…:Analyzers:0`). Deshalb sind Listen bisher bewusst env-only
(`ProjectTokens`, `Ui:Admins`, `Dast:Projects`).

## Ziel

SAST und DAST vollständig über die Settings-UI bedienbar machen: an/aus, welche Analyzer,
welche Projekte — inklusive der dafür nötigen generischen Listen-Unterstützung im
Settings-Modell.

## Nicht-Ziele

- `ProjectTokens` und `Ui:Admins` auf das neue Listen-Modell umstellen (bleiben env-only —
  Tokens gehören nicht in die gleiche Bearbeitungsmaske wie Scan-Schalter).
- `Naudit:Sast:OpengrepRules` DB-verwaltet machen (Pfade innerhalb des Images; env-only).
- Änderungen an `SastOptions`, `DastOptions` oder `AddNauditInfrastructure`.

## Architektur

### 1. Listen-Keys im Settings-Modell

`SettingDefinition` bekommt zwei optionale Felder:

```csharp
public sealed record SettingDefinition(
    string Key,
    bool IsSecret,
    bool IsList = false,
    IReadOnlyList<string>? AllowedValues = null);
```

Bestehende Call-Sites (`new("Naudit:Ai:Provider", false)`) bleiben unverändert.

**Speicherung:** Die DB hält weiterhin **genau eine Zeile pro Key**. Listenwerte stehen dort
als Komma-Liste (`"opengrep,trivy"`). `SettingsService.SetAsync` normalisiert beim Schreiben:
an `,` splitten, trimmen, Leereinträge verwerfen, mit `,` joinen. Bleibt danach nichts übrig,
verhält sich das Set wie ein Remove (Key fällt auf den Default zurück).

**Laden:** `DbSettingsLoader.Load` expandiert Listen-Keys nach dem Entschlüsseln in indizierte
Config-Keys:

```
Zeile:  Naudit:Sast:Analyzers = "opengrep,trivy"
Dict:   Naudit:Sast:Analyzers:0 = "opengrep"
        Naudit:Sast:Analyzers:1 = "trivy"
```

Der Elternkey wird **nicht** abgelegt — Binding auf `List<string>` liest nur Kinder, und ein
zusätzlicher Elternwert wäre irreführender Ballast. Damit sieht `AddNauditInfrastructure`
ausschließlich normales Config-Binding und bleibt unangetastet.

### 2. `SettingsValues` — der eine Ort, an dem Listen anders sind

Ein neuer statischer Helfer in `src/Naudit.Infrastructure/Settings/` kapselt die zwei Stellen,
an denen ein Listen-Key sich anders liest als ein Skalar:

- `Read(IConfiguration config, SettingDefinition def)` → sichtbarer Wert. Für Listen aus
  `config.GetSection(key).GetChildren()` gelesen und als CSV zurückgegeben (`config[key]` ist
  bei Listen immer `null`).
- `IsSet(IConfiguration config, SettingDefinition def)` → `config[key] is not null` **oder**
  (bei Listen) mindestens ein Kind unter der Section.
- `Normalize(string value)` → Split/Trim/Join für die CSV-Normalisierung.

`IsSet` ist die sicherheitsrelevante Hälfte: `Naudit__Sast__Analyzers__0=trivy` setzt **keinen**
Wert am Elternkey. Ohne die Section-Prüfung würde die UI eine env-gesetzte Liste als editierbar
anzeigen, den DB-Wert speichern — und die Umgebung würde ihn trotzdem überstimmen.

### 3. Katalog-Zuwachs

| Key | Art | Anmerkung |
| --- | --- | --- |
| `Naudit:Sast:Enabled` | Skalar (bool) | globaler SAST-Schalter |
| `Naudit:Sast:Analyzers` | **Liste** | `AllowedValues`: `opengrep`, `betterleaks`, `osv-scanner`, `trivy`, `dotnet-sca` |
| `Naudit:Sast:AnalyzerTimeout` | Skalar | Raw keys |
| `Naudit:Sast:MaxFindingsPerGroup` | Skalar | Raw keys |
| `Naudit:Sast:Reducer` | Skalar | `AllowedValues`: `deterministic` |
| `Naudit:Review:Dast:Projects` | **Liste** | freie Werte (`owner/repo` bzw. GitLab-Projekt-Id) |

Die `AllowedValues` für Analyzer sind die `case`-Labels des Switch in
`DependencyInjection.cs`; dieser vergleicht über `name.ToLowerInvariant()`, ist also
case-insensitiv — ein *unbekannter* Name wirft dort `InvalidOperationException`, eine
abweichende Schreibweise nicht. `SettingsService` schreibt Werte trotzdem in der Schreibweise
des Katalogs, weil die WebUI Analyzer-Namen exakt vergleicht (Checkbox-Zustand).

### 4. API

`GET /api/settings` liefert je Eintrag zusätzlich:

- `kind`: `"scalar"` | `"list"`
- `allowedValues`: `string[] | null`
- `value` für Listen als CSV (über `SettingsValues.Read`)
- `isSet`/`source`/`editable` über `SettingsValues.IsSet` statt `config[key]`/`env.Root[key]`

`PUT /api/settings` validiert neu **gegen `AllowedValues`**, bei Listen jeden Eintrag einzeln,
case-insensitiv; ungültig ⇒ 400 mit Nennung des Werts, **bevor** irgendetwas geschrieben wird
(die bestehende Zwei-Phasen-Schleife bleibt).

Das ist nicht nur Kosmetik: ein Tippfehler wie `trivvy` lässt `AddNauditInfrastructure` beim
nächsten Start werfen, und der Host käme nur noch im **Recovery-Modus** hoch. Die Validierung
macht diesen Selbstschuss unmöglich.

### 5. UI — zwei Panels in „Review rules"

`ReviewCategory.tsx` komponiert künftig nur noch; die Panels ziehen nach
`categories/review/` (`MergeGatePanel`, `RoundtripPanel`, `PromptPanel`, neu `SastPanel`,
`DastPanel`), damit keine 250-Zeilen-Datei entsteht.

**Static analysis (SAST)**
- `Toggle` für `Naudit:Sast:Enabled`.
- Checkbox-Reihe für die Analyzer, Optionen aus `allowedValues` der API (nicht hartcodiert).
- Ungesetzter Key ⇒ die DI-Defaults `opengrep`/`trivy` erscheinen vorausgewählt mit Hinweis
  „default".
- Alles abgewählt ⇒ der Key wird entfernt (= Defaults), mit sichtbarem Hinweis. „SAST an, aber
  kein Tool" ist kein ausdrückbarer Zustand; wer nichts will, schaltet den Toggle aus. Damit
  bleibt die DI-Semantik (leere Liste ⇒ Defaults) unverändert und die UI lügt nicht.

**Dynamic testing (DAST)**
- `Toggle` für `Naudit:Review:Dast:Enabled`.
- Textarea für `Naudit:Review:Dast:Projects`, eine Zeile pro Projekt (↔ CSV in der API).
- `DockerfilePath`, `AppPort`, `HealthPath` als Felder — ohne die läuft DAST nicht sinnvoll;
  die übrigen Dast-Keys bleiben in „Raw keys".
- Warnhinweis: DAST **baut und startet fremden PR-Code** und setzt den gemounteten
  Docker-Socket voraus (`docs/dast.md`).
- Ist DAST an und die Allowlist leer, zeigt das Panel eine Warnung — der Zustand ist
  fail-closed und läuft nie, das soll nicht still passieren.

**Raw keys**
- Listen-Keys als CSV-Textfeld mit Hinweis „comma-separated".
- Die lokale `ENUMS`-Tabelle weicht den `allowedValues` aus der API, wo vorhanden (die
  UI-only-Enums wie `Ui:Auth:*:Enabled` bleiben lokal).

**Sidebar-Hinweis** der Kategorie „Review rules" zeigt zusätzlich den Scan-Status
(`sast on · dast off`).

Alle Änderungen greifen wie gehabt erst nach dem Neustart; der bestehende
„restart required"-Banner deckt das ab.

## Fehlerverhalten

- Unbekannter Analyzer-Name ⇒ 400 aus dem PUT, kein Schreiben (siehe §4).
- Nicht entschlüsselbarer Wert ⇒ unverändert Warnung + „gilt als fehlend" (Listen sind keine
  Secrets, betrifft sie faktisch nicht).
- Env-gesetzte Liste ⇒ gesperrt angezeigt, PUT antwortet 400 wie bei Skalaren.
- Leere/whitespace-Einträge in der CSV ⇒ beim Normalisieren verworfen, nie als leerer
  Analyzer-Name an die DI durchgereicht.

## Tests

- `DbSettingsLoaderTests`: Listen-Key wird zu indizierten Keys expandiert; `SastOptions`/
  `DastOptions` binden daraus korrekt; Leer-/Whitespace-Einträge fallen raus; Elternkey wird
  nicht gesetzt.
- `SettingsServiceTests`: CSV-Normalisierung beim Schreiben, Round-trip, leere Liste ⇒ Remove.
- `SettingsEndpointTests`: `kind`/`allowedValues` im GET; CSV-Wert im GET für Listen; 400 bei
  unbekanntem Analyzer; Env-Lock bei indiziert gesetzter Liste (`Naudit__Sast__Analyzers__0`).
- Frontend: `npm run lint && npm run build`.

## Dokumentation

- `docs/configuration.md`: Listen-Keys im DB-Settings-Modell (CSV-Zeile ⇒ indizierte Keys).
- `docs/dast.md`: `Projects` ist nicht mehr env-only.
- `CLAUDE.md`: Katalog-Beschreibung („Listen-Keys wie ProjectTokens/Ui:Admins bleiben env-only")
  nachziehen.
