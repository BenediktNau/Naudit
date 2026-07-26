# SAST/DAST in den Settings — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** SAST (an/aus + Analyzer-Auswahl) und DAST (an/aus + Projekt-Allowlist) über die
Settings-UI bedienbar machen, inklusive generischer Listen-Unterstützung im DB-Settings-Modell.

**Architecture:** `SettingDefinition` bekommt `IsList`/`AllowedValues`. Die DB hält weiterhin eine
Zeile pro Key; Listen stehen dort als CSV und werden von `DbSettingsLoader` in indizierte
Config-Keys (`…:Analyzers:0`) expandiert, bevor sie als `MemoryConfigurationSource` in die
Konfiguration gehen. `SastOptions`, `DastOptions` und `AddNauditInfrastructure` bleiben
unverändert — sie sehen normales Config-Binding.

**Tech Stack:** .NET 10, EF Core, ASP.NET Minimal API, xUnit; React 19 + TS + Tailwind 4 (Vite).

## Global Constraints

- Solution-Datei ist `Naudit.slnx` (nicht `.sln`).
- Code-Kommentare auf Deutsch, Test-Namen auf Deutsch im Stil der bestehenden Tests.
- Core-Regel: keine Änderung an `Naudit.Core`; alles in Infrastructure/Web/Frontend.
- Keine Änderung an `SastOptions`, `DastOptions`, `AddNauditInfrastructure`.
- TDD: pro Task rot → grün → ein Commit.
- Spec: `docs/superpowers/specs/2026-07-26-settings-sast-dast-design.md`.

---

### Task 1: Listen-fähiges Settings-Modell (`SettingDefinition` + `SettingsValues` + Katalog)

**Files:**
- Modify: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`
- Create: `src/Naudit.Infrastructure/Settings/SettingsValues.cs`
- Test: `tests/Naudit.Tests/SettingsValuesTests.cs`

**Interfaces:**
- Produces: `SettingDefinition(string Key, bool IsSecret, bool IsList = false, IReadOnlyList<string>? AllowedValues = null)`;
  `SettingsValues.Split(string) → IEnumerable<string>`, `SettingsValues.Normalize(string) → string`,
  `SettingsValues.Read(IConfiguration, SettingDefinition) → string?`,
  `SettingsValues.IsSet(IConfiguration, SettingDefinition) → bool`.
- Consumes: nichts.

- [ ] **Step 1: Write the failing test** — `tests/Naudit.Tests/SettingsValuesTests.cs`

```csharp
using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

/// <summary>Der eine Ort, an dem sich Listen-Keys anders lesen als Skalare: CSV ⇄ indizierte
/// Config-Keys, plus die Env-Erkennung (Naudit__Sast__Analyzers__0 setzt KEINEN Elternwert).</summary>
public class SettingsValuesTests
{
    private static readonly SettingDefinition ListDef =
        new("Naudit:Sast:Analyzers", false, IsList: true);
    private static readonly SettingDefinition ScalarDef = new("Naudit:Ai:Model", false);

    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    [Fact]
    public void Normalize_trimmtUndVerwirftLeereEintraege()
    {
        Assert.Equal("opengrep,trivy", SettingsValues.Normalize(" opengrep , ,trivy "));
        Assert.Equal("", SettingsValues.Normalize("  ,  "));
    }

    [Fact]
    public void Read_liesListeAusIndiziertenKeysAlsCsv()
    {
        var config = Config(("Naudit:Sast:Analyzers:0", "opengrep"), ("Naudit:Sast:Analyzers:1", "trivy"));
        Assert.Equal("opengrep,trivy", SettingsValues.Read(config, ListDef));
    }

    [Fact]
    public void Read_ungesetzteListe_istNull()
        => Assert.Null(SettingsValues.Read(Config(), ListDef));

    [Fact]
    public void Read_skalar_liestDenKeyDirekt()
        => Assert.Equal("m", SettingsValues.Read(Config(("Naudit:Ai:Model", "m")), ScalarDef));

    [Fact]
    public void IsSet_liste_erkenntIndizierteKinderOhneElternwert()
    {
        var config = Config(("Naudit:Sast:Analyzers:0", "trivy"));
        Assert.Null(config["Naudit:Sast:Analyzers"]);   // genau die Falle
        Assert.True(SettingsValues.IsSet(config, ListDef));
        Assert.False(SettingsValues.IsSet(Config(), ListDef));
    }

    [Fact]
    public void Katalog_kenntSastUndDastListen()
    {
        Assert.True(SettingsCatalog.TryGet("Naudit:Sast:Enabled", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Sast:Analyzers", out var analyzers));
        Assert.True(analyzers.IsList);
        Assert.Contains("opengrep", analyzers.AllowedValues!);
        Assert.Contains("dotnet-sca", analyzers.AllowedValues!);
        Assert.True(SettingsCatalog.TryGet("Naudit:Review:Dast:Projects", out var projects));
        Assert.True(projects.IsList);
        Assert.Null(projects.AllowedValues);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SettingsValuesTests`
Expected: Build-Fehler — `SettingsValues` existiert nicht, `IsList` ist kein Parameter.

- [ ] **Step 3: Write the implementation** — `SettingsValues.cs`

```csharp
using Microsoft.Extensions.Configuration;

namespace Naudit.Infrastructure.Settings;

/// <summary>Lese-/Schreibhilfen für Katalog-Werte. Skalare sind trivial; Listen liegen in der DB
/// als eine CSV-Zeile und in der Config als indizierte Kind-Keys (…:0, …:1) — genau die zwei
/// Stellen, an denen sich das unterscheidet, stehen hier und sonst nirgends.</summary>
public static class SettingsValues
{
    public static IEnumerable<string> Split(string value)
        => value.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0);

    public static string Normalize(string value) => string.Join(",", Split(value));

    /// <summary>Sichtbarer Wert für die Settings-API. Listen werden als CSV zurückgegeben —
    /// config[key] ist bei Listen IMMER null, der Wert steht in den Kind-Keys.</summary>
    public static string? Read(IConfiguration config, SettingDefinition definition)
    {
        if (!definition.IsList) return config[definition.Key];
        var items = config.GetSection(definition.Key).GetChildren()
            .Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        return items.Count == 0 ? null : string.Join(",", items);
    }

    /// <summary>Ist der Key in DIESER Config-Quelle gesetzt? Für Listen zählt jedes Kind:
    /// Naudit__Sast__Analyzers__0=trivy setzt keinen Elternwert, wäre also sonst unsichtbar —
    /// und die UI würde einen env-gesetzten Key fälschlich als editierbar anbieten.</summary>
    public static bool IsSet(IConfiguration config, SettingDefinition definition)
        => definition.IsList
            ? config.GetSection(definition.Key).GetChildren().Any()
            : config[definition.Key] is not null;
}
```

- [ ] **Step 4: Katalog erweitern** — `SettingsCatalog.cs`

`SettingDefinition` ersetzen:

```csharp
/// <summary>Ein DB-verwaltbarer Konfigurationswert. IsSecret steuert Verschlüsselung und
/// Write-only-Verhalten der Settings-API. IsList ⇒ eine CSV-Zeile in der DB, die der
/// DbSettingsLoader zu indizierten Config-Keys expandiert. AllowedValues ⇒ die Settings-API
/// lehnt alles andere ab (ein ungültiger Wert würde den nächsten Start in den Recovery-Modus
/// zwingen).</summary>
public sealed record SettingDefinition(
    string Key,
    bool IsSecret,
    bool IsList = false,
    IReadOnlyList<string>? AllowedValues = null);
```

Klassendoku anpassen (Listen sind nicht mehr grundsätzlich env-only):

```csharp
/// <summary>Whitelist der DB-verwaltbaren Keys. Bootstrap-Keys (Naudit:Db:*, ForwardedHeaders,
/// Ports) fehlen hier bewusst — sie müssen vor dem DB-Zugriff bekannt sein und bleiben env-only.
/// Listen-Keys sind über IsList möglich (CSV-Zeile ⇒ indizierte Config-Keys); ProjectTokens und
/// Ui:Admins bleiben trotzdem env-only — Zugangsdaten gehören nicht in dieselbe Maske.</summary>
```

Einträge ergänzen (SAST-Block direkt vor dem Review-Block, `Projects` direkt nach `Dast:Enabled`):

```csharp
        new("Naudit:Sast:Enabled", false),
        new("Naudit:Sast:Analyzers", false, IsList: true,
            AllowedValues: ["opengrep", "betterleaks", "osv-scanner", "trivy", "dotnet-sca"]),
        new("Naudit:Sast:AnalyzerTimeout", false),
        new("Naudit:Sast:MaxFindingsPerGroup", false),
        new("Naudit:Sast:Reducer", false, AllowedValues: ["deterministic"]),
```

```csharp
        new("Naudit:Review:Dast:Projects", false, IsList: true),
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SettingsValuesTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Settings/ tests/Naudit.Tests/SettingsValuesTests.cs
git commit -m "feat(settings): Listen-Keys im Katalog (IsList/AllowedValues) + SettingsValues"
```

---

### Task 2: CSV ⇄ indizierte Keys in Persistenz und Bootstrap

**Files:**
- Modify: `src/Naudit.Infrastructure/Settings/SettingsService.cs`
- Modify: `src/Naudit.Infrastructure/Settings/DbSettingsLoader.cs:38-56` (Leseschleife)
- Test: `tests/Naudit.Tests/SettingsServiceTests.cs`, `tests/Naudit.Tests/DbSettingsLoaderTests.cs`

**Interfaces:**
- Consumes: `SettingsValues.Normalize/Split`, `SettingDefinition.IsList` (Task 1).
- Produces: `DbSettingsLoadResult.Settings` enthält für Listen **nur** indizierte Keys
  (`Naudit:Sast:Analyzers:0`), nie den Elternkey.

- [ ] **Step 1: Write the failing tests**

In `tests/Naudit.Tests/SettingsServiceTests.cs` ergänzen:

```csharp
    [Fact]
    public async Task SetAsync_liste_wirdNormalisiertGespeichert()
    {
        using var fx = new Fixture();
        await fx.Service.SetAsync("Naudit:Sast:Analyzers", " opengrep , ,trivy ");
        var row = fx.Db.Settings.Single(s => s.Key == "Naudit:Sast:Analyzers");
        Assert.Equal("opengrep,trivy", row.Value);
        Assert.False(row.IsSecret);
    }

    [Fact]
    public async Task SetAsync_leereListe_entferntDenKey()
    {
        using var fx = new Fixture();
        await fx.Service.SetAsync("Naudit:Sast:Analyzers", "trivy");
        await fx.Service.SetAsync("Naudit:Sast:Analyzers", " , ");
        Assert.Empty(fx.Db.Settings.Where(s => s.Key == "Naudit:Sast:Analyzers"));
    }
```

> Die Hilfsklasse `Fixture` (bzw. das im File bereits vorhandene Setup-Muster für
> `SettingsService` + `NauditDbContext`) wiederverwenden — nicht neu erfinden. Falls die Datei
> ein anderes Muster nutzt (z. B. `CreateService()`), die beiden Tests daran anpassen.

In `tests/Naudit.Tests/DbSettingsLoaderTests.cs` ergänzen:

```csharp
    [Fact]
    public void Load_listenKey_wirdZuIndiziertenConfigKeysExpandiert()
    {
        DbSettingsLoader.Load(Options);
        WriteViaService(svc =>
        {
            svc.SetAsync("Naudit:Sast:Analyzers", "opengrep, trivy").GetAwaiter().GetResult();
            svc.SetAsync("Naudit:Review:Dast:Projects", "acme/web").GetAwaiter().GetResult();
        });

        var result = DbSettingsLoader.Load(Options);

        Assert.Equal("opengrep", result.Settings["Naudit:Sast:Analyzers:0"]);
        Assert.Equal("trivy", result.Settings["Naudit:Sast:Analyzers:1"]);
        Assert.Equal("acme/web", result.Settings["Naudit:Review:Dast:Projects:0"]);
        // Elternkey bleibt leer: List<string>-Binding liest ausschliesslich Kinder.
        Assert.False(result.Settings.ContainsKey("Naudit:Sast:Analyzers"));
    }

    [Fact]
    public void Load_listenKey_bindetAufSastOptions()
    {
        DbSettingsLoader.Load(Options);
        WriteViaService(svc => svc.SetAsync("Naudit:Sast:Analyzers", "trivy,dotnet-sca").GetAwaiter().GetResult());

        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(DbSettingsLoader.Load(Options).Settings).Build();
        var options = config.GetSection("Naudit:Sast")
            .Get<Naudit.Infrastructure.Sast.SastOptions>()!;

        Assert.Equal(["trivy", "dotnet-sca"], options.Analyzers);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "SettingsServiceTests|DbSettingsLoaderTests"`
Expected: FAIL — der Loader legt heute `Naudit:Sast:Analyzers` als Elternkey ab, `Analyzers` bleibt leer.

- [ ] **Step 3: `SettingsService.SetAsync` normalisieren**

Direkt nach dem Katalog-Lookup einfügen:

```csharp
        if (def.IsList)
        {
            // Listen liegen als EINE CSV-Zeile in der DB; leer nach dem Normalisieren heisst
            // "zurück auf Default" — sonst stünde dort eine Zeile mit leerem Wert.
            value = SettingsValues.Normalize(value);
            if (value.Length == 0) { await RemoveAsync(def.Key, ct); return; }
        }
```

- [ ] **Step 4: `DbSettingsLoader` expandieren**

Die Zuweisungen in der Leseschleife über einen lokalen Helfer führen (statt `settings[row.Key] = …`):

```csharp
            if (!row.IsSecret) { Store(definition, row.Value); continue; }
            try { Store(definition, protector.Unprotect(row.Value)); }
```

`definition` kommt aus dem bestehenden `TryGet` (`out var definition` statt `out _`). Am Ende der
Methode, vor `return`:

```csharp
        void Store(SettingDefinition definition, string value)
        {
            if (!definition.IsList) { settings[definition.Key] = value; return; }
            // Listen: CSV ⇒ indizierte Kind-Keys, sonst bindet List<string> nichts.
            var index = 0;
            foreach (var item in SettingsValues.Split(value))
                settings[$"{definition.Key}:{index++}"] = item;
        }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "SettingsServiceTests|DbSettingsLoaderTests"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Settings/ tests/Naudit.Tests/
git commit -m "feat(settings): Listen-Werte als CSV speichern und beim Bootstrap expandieren"
```

---

### Task 3: Settings-API — `kind`/`allowedValues` und Wert-Validierung

**Files:**
- Modify: `src/Naudit.Web/Endpoints/SettingsEndpoints.cs`
- Test: `tests/Naudit.Tests/SettingsEndpointTests.cs`

**Interfaces:**
- Consumes: `SettingsValues.Read/IsSet/Split`, `SettingDefinition.IsList/AllowedValues`.
- Produces: JSON je Setting: `{ key, isSecret, isSet, source, editable, value, kind, allowedValues }`
  mit `kind` ∈ `"scalar" | "list"`; `value` bei Listen als CSV.

- [ ] **Step 1: Write the failing tests** — in `SettingsEndpointTests.cs` ergänzen

```csharp
    [Fact]
    public async Task Get_liefertKindUndAllowedValues()
    {
        var (client, _) = CreateLoggedInAdmin();
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/settings"));
        var settings = doc.RootElement.GetProperty("settings").EnumerateArray().ToList();

        var analyzers = settings.Single(s => s.GetProperty("key").GetString() == "Naudit:Sast:Analyzers");
        Assert.Equal("list", analyzers.GetProperty("kind").GetString());
        Assert.Contains("trivy", analyzers.GetProperty("allowedValues").EnumerateArray().Select(v => v.GetString()));

        var model = settings.Single(s => s.GetProperty("key").GetString() == "Naudit:Ai:Model");
        Assert.Equal("scalar", model.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, model.GetProperty("allowedValues").ValueKind);
    }

    [Fact]
    public async Task Put_unbekannterAnalyzer_wirdAbgelehnt()
    {
        var (client, restarter) = CreateLoggedInAdmin();
        var res = await client.PutAsJsonAsync("/api/settings", new
        {
            changes = new[] { new { key = "Naudit:Sast:Analyzers", value = (string?)"trivy,trivvy" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("trivvy", await res.Content.ReadAsStringAsync());
        Assert.False(restarter.RestartPending); // nichts geschrieben, kein Restart angefordert
    }

    [Fact]
    public async Task Put_gueltigeAnalyzerListe_wirdGespeichert()
    {
        var (client, restarter) = CreateLoggedInAdmin();
        var res = await client.PutAsJsonAsync("/api/settings", new
        {
            changes = new[] { new { key = "Naudit:Sast:Analyzers", value = (string?)"trivy, dotnet-sca" } },
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(restarter.RestartPending);

        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/settings"));
        var analyzers = doc.RootElement.GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "Naudit:Sast:Analyzers");
        Assert.Equal("db", analyzers.GetProperty("source").GetString());
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SettingsEndpointTests`
Expected: FAIL — `kind` fehlt in der Antwort, der ungültige Analyzer wird mit 200 akzeptiert.

- [ ] **Step 3: GET erweitern** — Projektion in `MapGet` ersetzen

```csharp
                settings = SettingsCatalog.All.Select(def =>
                {
                    var envLocked = SettingsValues.IsSet(env.Root, def);
                    var isSet = envLocked || dbKeys.Contains(def.Key) || SettingsValues.IsSet(config, def);
                    return new
                    {
                        key = def.Key,
                        isSecret = def.IsSecret,
                        isSet,
                        source = envLocked ? "env" : dbKeys.Contains(def.Key) ? "db" : "default",
                        editable = !envLocked,
                        value = def.IsSecret ? null : SettingsValues.Read(config, def),
                        kind = def.IsList ? "list" : "scalar",
                        allowedValues = def.AllowedValues,
                    };
                }),
```

- [ ] **Step 4: PUT validieren** — Validierungsschleife ersetzen

```csharp
            // Erst komplett validieren, dann schreiben — keine halb angewendeten Batches.
            foreach (var change in body.Changes)
            {
                if (!SettingsCatalog.TryGet(change.Key, out var def))
                    return Results.BadRequest(new { error = $"'{change.Key}' is not a managed setting." });
                if (SettingsValues.IsSet(env.Root, def))
                    return Results.BadRequest(new { error = $"'{change.Key}' is set via environment and cannot be edited here." });
                if (change.Value is null || def.AllowedValues is not { } allowed) continue;
                // Ungültige Werte würden erst beim nächsten Start auffallen — und den Host dann
                // in den Recovery-Modus zwingen. Deshalb hier hart ablehnen.
                var candidates = def.IsList ? SettingsValues.Split(change.Value) : [change.Value];
                foreach (var candidate in candidates)
                    if (!allowed.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                        return Results.BadRequest(new { error = $"'{candidate}' is not a valid value for '{change.Key}'. Allowed: {string.Join(", ", allowed)}." });
            }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SettingsEndpointTests`
Expected: PASS

- [ ] **Step 6: Full suite + Commit**

```bash
dotnet test Naudit.slnx
git add src/Naudit.Web/Endpoints/SettingsEndpoints.cs tests/Naudit.Tests/SettingsEndpointTests.cs
git commit -m "feat(settings-api): kind/allowedValues im GET, Wert-Validierung im PUT"
```

---

### Task 4: Frontend — SAST-/DAST-Panels in „Review rules"

**Files:**
- Modify: `src/frontend/src/api/types.ts:103-110` (`SettingItem`)
- Modify: `src/frontend/src/components/settings/model.ts` (`SettingsCtx.options`)
- Modify: `src/frontend/src/components/pages/SettingsPage.tsx` (ctx um `options` erweitern)
- Modify: `src/frontend/src/components/settings/categories/ReviewCategory.tsx` (komponiert nur noch)
- Create: `src/frontend/src/components/settings/categories/review/MergeGatePanel.tsx`
- Create: `src/frontend/src/components/settings/categories/review/RoundtripPanel.tsx`
- Create: `src/frontend/src/components/settings/categories/review/PromptPanel.tsx`
- Create: `src/frontend/src/components/settings/categories/review/SastPanel.tsx`
- Create: `src/frontend/src/components/settings/categories/review/DastPanel.tsx`
- Modify: `src/frontend/src/components/settings/RawKeys.tsx` (Listen + `allowedValues`)
- Modify: `src/frontend/src/components/settings/hints.ts` (Scan-Status im Review-Hinweis)

**Interfaces:**
- Consumes: `SettingItem.kind`, `SettingItem.allowedValues` (Task 3).
- Produces: `SettingsCtx.options(key: string): string[]`.

- [ ] **Step 1: Typen + Kontext erweitern**

`types.ts`:

```ts
export interface SettingItem {
  key: string;
  isSecret: boolean;
  isSet: boolean;
  source: "db" | "env" | "default";
  editable: boolean;
  value: string | null;
  kind: "scalar" | "list";
  allowedValues: string[] | null;
}
```

`model.ts` — in `SettingsCtx`:

```ts
  options(key: string): string[];  // allowedValues aus dem Katalog, [] wenn frei
```

`SettingsPage.tsx` — im `ctx`-Memo:

```ts
    options: (k) => byKey.get(k)?.allowedValues ?? [],
```

- [ ] **Step 2: `ReviewCategory` aufteilen**

Die drei bestehenden Panels 1:1 nach `categories/review/MergeGatePanel.tsx`,
`RoundtripPanel.tsx`, `PromptPanel.tsx` verschieben (Inhalt unverändert, jeweils
`export function XPanel({ ctx }: { ctx: SettingsCtx })`, `selCls` nach
`categories/review/shared.ts`). `ReviewCategory.tsx` wird zu:

```tsx
import type { SettingsCtx } from "../model";
import { MergeGatePanel } from "./review/MergeGatePanel";
import { RoundtripPanel } from "./review/RoundtripPanel";
import { PromptPanel } from "./review/PromptPanel";
import { SastPanel } from "./review/SastPanel";
import { DastPanel } from "./review/DastPanel";

export function ReviewCategory({ ctx }: { ctx: SettingsCtx }) {
  return (
    <>
      <MergeGatePanel ctx={ctx} />
      <RoundtripPanel ctx={ctx} />
      <PromptPanel ctx={ctx} />
      <SastPanel ctx={ctx} />
      <DastPanel ctx={ctx} />
    </>
  );
}
```

- [ ] **Step 3: `SastPanel.tsx` schreiben**

```tsx
import { Panel } from "@/components/ui/Panel";
import { Toggle } from "../../primitives";
import type { SettingsCtx } from "../../model";

const KEY_ENABLED = "Naudit:Sast:Enabled";
const KEY_ANALYZERS = "Naudit:Sast:Analyzers";
/** Fällt der Key weg, registriert die DI genau diese zwei — die UI zeigt das statt zu luegen. */
const DEFAULTS = ["opengrep", "trivy"];

export function SastPanel({ ctx }: { ctx: SettingsCtx }) {
  const on = ctx.get(KEY_ENABLED) === "true";
  const raw = ctx.get(KEY_ANALYZERS).split(",").map((s) => s.trim()).filter(Boolean);
  const isDefault = raw.length === 0;
  const selected = isDefault ? DEFAULTS : raw;
  const locked = ctx.locked(KEY_ANALYZERS);

  const toggleAnalyzer = (name: string) => {
    const next = selected.includes(name) ? selected.filter((s) => s !== name) : [...selected, name];
    // Leer heisst "Key entfernen" ⇒ Defaults. "An, aber kein Tool" gibt es nicht.
    ctx.set(KEY_ANALYZERS, next.join(","));
  };

  return (
    <Panel title="Static analysis (SAST)" extra={on ? "on" : "off"}>
      <div className="flex flex-col gap-4 px-5 py-4">
        <div className="flex items-center justify-between gap-4">
          <div>
            <div className="text-[13px] font-medium text-ink">Scan the diff with static analyzers</div>
            <p className="mt-0.5 text-[12.5px] text-ink2">
              Findings are added to the prompt as grounding. They never block a merge on their own.
            </p>
          </div>
          <Toggle on={on} disabled={ctx.locked(KEY_ENABLED)} aria-label="Enable SAST"
            onChange={(v) => ctx.set(KEY_ENABLED, String(v))} />
        </div>

        <div className="flex flex-col gap-2">
          <div className="flex items-center gap-2">
            <span className="text-[13px] font-medium text-ink">Analyzers</span>
            {isDefault && <span className="font-mono text-[11px] text-ink3">default</span>}
          </div>
          <div className="flex flex-wrap gap-2">
            {ctx.options(KEY_ANALYZERS).map((name) => (
              <label key={name}
                className={`flex items-center gap-2 rounded-lg border px-3 py-2 font-mono text-[12.5px] ${
                  selected.includes(name) ? "border-acc bg-acc/6 text-ink" : "border-border text-ink2"
                } ${locked || !on ? "opacity-50" : "cursor-pointer"}`}>
                <input type="checkbox" checked={selected.includes(name)} disabled={locked || !on}
                  onChange={() => toggleAnalyzer(name)} />
                {name}
              </label>
            ))}
          </div>
          <p className="text-[12.5px] text-ink2">
            Unchecking everything falls back to the defaults ({DEFAULTS.join(", ")}). To run no
            analysis at all, switch SAST off.
          </p>
        </div>
      </div>
    </Panel>
  );
}
```

- [ ] **Step 4: `DastPanel.tsx` schreiben**

```tsx
import { Panel } from "@/components/ui/Panel";
import { Field } from "@/components/setup/shared";
import { Toggle } from "../../primitives";
import type { SettingsCtx } from "../../model";
import { selCls } from "./shared";

const KEY_ENABLED = "Naudit:Review:Dast:Enabled";
const KEY_PROJECTS = "Naudit:Review:Dast:Projects";

export function DastPanel({ ctx }: { ctx: SettingsCtx }) {
  const on = ctx.get(KEY_ENABLED) === "true";
  const projects = ctx.get(KEY_PROJECTS).split(",").map((s) => s.trim()).filter(Boolean);

  return (
    <Panel title="Dynamic testing (DAST)" extra={on ? `${projects.length} project(s)` : "off"}>
      <div className="flex flex-col gap-4 px-5 py-4">
        <div className="flex items-center justify-between gap-4">
          <div>
            <div className="text-[13px] font-medium text-ink">Build and probe the PR's app</div>
            <p className="mt-0.5 text-[12.5px] text-ink2">
              Runs the pull request's own Dockerfile in an isolated container and probes it through
              a browser. Requires the host Docker socket to be mounted.
            </p>
          </div>
          <Toggle on={on} disabled={ctx.locked(KEY_ENABLED)} aria-label="Enable DAST"
            onChange={(v) => ctx.set(KEY_ENABLED, String(v))} />
        </div>

        <Field label="Allowed projects" hint="One per line — owner/repo (GitHub) or the GitLab project id. Empty means no project runs.">
          <textarea rows={3} disabled={ctx.locked(KEY_PROJECTS)}
            className="min-h-[72px] w-full rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc disabled:opacity-50"
            placeholder="acme/web"
            value={projects.join("\n")}
            onChange={(e) => ctx.set(KEY_PROJECTS, e.target.value.split("\n").map((s) => s.trim()).filter(Boolean).join(","))} />
        </Field>

        {on && projects.length === 0 && (
          <div className="rounded-lg border border-warn/40 bg-warn/8 px-4 py-3 text-[12.5px] text-ink2">
            DAST is on but no project is allowlisted — nothing will run. This is deliberate:
            dynamic testing executes untrusted pull-request code, so it is opt-in per project.
          </div>
        )}

        <div className="flex flex-wrap gap-4">
          <Field label="Dockerfile path" hint="Relative to the repo root.">
            <input className={selCls} placeholder="Dockerfile (default)"
              disabled={ctx.locked("Naudit:Review:Dast:DockerfilePath")}
              value={ctx.get("Naudit:Review:Dast:DockerfilePath")}
              onChange={(e) => ctx.set("Naudit:Review:Dast:DockerfilePath", e.target.value)} />
          </Field>
          <Field label="App port" hint="Port the app listens on.">
            <input type="number" className={selCls} placeholder="8080 (default)"
              disabled={ctx.locked("Naudit:Review:Dast:AppPort")}
              value={ctx.get("Naudit:Review:Dast:AppPort")}
              onChange={(e) => ctx.set("Naudit:Review:Dast:AppPort", e.target.value)} />
          </Field>
          <Field label="Health path" hint="Polled until the app answers.">
            <input className={selCls} placeholder="/ (default)"
              disabled={ctx.locked("Naudit:Review:Dast:HealthPath")}
              value={ctx.get("Naudit:Review:Dast:HealthPath")}
              onChange={(e) => ctx.set("Naudit:Review:Dast:HealthPath", e.target.value)} />
          </Field>
        </div>
      </div>
    </Panel>
  );
}
```

- [ ] **Step 5: `RawKeys.tsx` — Listen und `allowedValues`**

`ENUMS` bleibt als Fallback für UI-Enums; die Optionen kommen bevorzugt aus der API:

```tsx
  const options = item.allowedValues ?? ENUMS[item.key];
```

Für Listen-Keys das Freitextfeld mit Hinweis rendern (statt des `options`-Selects — eine Liste
mit `allowedValues` ist mehrwertig und passt nicht in ein `<select>`):

```tsx
      {!item.editable ? (
        <span className="font-mono text-[12.5px] text-ink3">{item.isSecret ? "•••" : item.value ?? "—"}</span>
      ) : item.kind === "list" ? (
        <input
          className="w-[300px] rounded border border-hairline bg-transparent px-2 py-1 font-mono text-[12.5px] text-ink2"
          placeholder="comma-separated"
          value={ctx.get(item.key)} onChange={(e) => ctx.set(item.key, e.target.value)}
        />
      ) : options ? (
```

- [ ] **Step 6: `hints.ts` — Scan-Status**

```ts
  const sast = ctx.get("Naudit:Sast:Enabled") === "true";
  const dast = ctx.get("Naudit:Review:Dast:Enabled") === "true";
  const scans = [sast && "sast", dast && "dast"].filter(Boolean).join(" · ");
```

und den `review`-Eintrag ersetzen:

```ts
    review: scans
      ? { tone: "acc", text: scans as string }
      : gateDefault ? { tone: "ink3", text: "defaults" } : { tone: "ink3", text: "custom" },
```

- [ ] **Step 7: Lint + Build**

Run: `cd src/frontend && npm ci && npm run lint && npm run build`
Expected: keine Fehler.

- [ ] **Step 8: Commit**

```bash
git add src/frontend
git commit -m "feat(webui): SAST-/DAST-Panels in den Review-Settings"
```

---

### Task 5: Dokumentation

**Files:**
- Modify: `docs/configuration.md` (Abschnitt zu DB-verwalteten Settings)
- Modify: `docs/dast.md:124-130` (Config-Absatz: `Projects` nicht mehr env-only)
- Modify: `docs/sast.md` (falls vorhanden — Hinweis auf Settings-Seite)
- Modify: `CLAUDE.md` (Config-Modell-Absatz: Listen-Keys)

- [ ] **Step 1: `docs/configuration.md`** — im DB-Settings-Abschnitt ergänzen

```markdown
### Listenförmige Settings

Keys wie `Naudit:Sast:Analyzers` und `Naudit:Review:Dast:Projects` sind Listen. In der DB stehen
sie als **eine** Zeile mit Komma-Liste (`opengrep,trivy`); beim Start expandiert der
`DbSettingsLoader` sie in indizierte Config-Keys (`Naudit:Sast:Analyzers:0`, `:1`, …), sodass das
normale Options-Binding greift. Per Umgebung gilt weiterhin die indizierte Schreibweise:

```
Naudit__Sast__Analyzers__0=opengrep
Naudit__Sast__Analyzers__1=trivy
```

Ist ein Listen-Key per Umgebung gesetzt — auch nur ein einzelner Index —, ist er in der WebUI
gesperrt; die Umgebung gewinnt wie bei jedem anderen Key. `Naudit:GitHub:ProjectTokens`,
`Naudit:GitLab:ProjectTokens` und `Naudit:Ui:Admins` bleiben bewusst env-only.
```

- [ ] **Step 2: `docs/dast.md`** — den Satz „`Projects` ist list-shaped und therefore
**env/appsettings-only** (indexed syntax), like `ProjectTokens`" ersetzen durch:

```markdown
`Projects` is list-shaped but DB-managed: the Settings page has a "Dynamic testing (DAST)" panel
(one project per line), and the value is stored as a single comma-separated row. Setting it via
environment still uses the indexed syntax (`Naudit__Review__Dast__Projects__0`) and locks the
field in the UI.
```

Ebenso die Zeile in der Config-Tabelle (`Projects … Env-only.`) auf „Settings page or indexed env
syntax." korrigieren.

- [ ] **Step 3: `CLAUDE.md`** — im Config-Modell-Absatz den Halbsatz
„(list-shaped keys like `ProjectTokens`/`Ui:Admins` and the admin seed stay env-only)" ersetzen
durch:

```markdown
(list-shaped keys are supported via `SettingDefinition.IsList` — one comma-separated DB row that
`DbSettingsLoader` expands into indexed config keys; `ProjectTokens`/`Ui:Admins` and the admin
seed still stay env-only)
```

und in der DAST-Sektion „Gated twice" ergänzen, dass beide Schalter jetzt über die Settings-Seite
bedienbar sind.

- [ ] **Step 4: Commit**

```bash
git add docs CLAUDE.md
git commit -m "docs: Listen-Settings + SAST/DAST auf der Settings-Seite"
```

---

## Self-Review

**Spec-Abdeckung:** §1 Listen-Modell → Task 1+2. §2 `SettingsValues` → Task 1. §3 Katalog →
Task 1. §4 API → Task 3. §5 UI → Task 4. Fehlerverhalten (400 bei ungültigem Wert, Env-Lock bei
indizierter Liste, Whitespace-Einträge) → Tests in Task 1–3. Doku → Task 5.

**Typkonsistenz:** `SettingsValues.Split/Normalize/Read/IsSet` werden in Task 2 und 3 exakt mit
den in Task 1 definierten Signaturen aufgerufen; `SettingItem.kind`/`allowedValues` in Task 4
entsprechen der Projektion aus Task 3.

**Offene Annahme:** `SettingsServiceTests` nutzt ein bestehendes Fixture-Muster — die zwei neuen
Tests daran anpassen statt ein zweites Muster einzuführen.
