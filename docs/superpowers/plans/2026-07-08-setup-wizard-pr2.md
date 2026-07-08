# Setup-Wizard (PR 2/3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** First-Run-Setup-Wizard: eine unkonfigurierte Naudit-Instanz erkennt fehlende Pflicht-Config, fährt im Setup-Modus hoch (keine Webhooks/Reviews) und führt den Admin in der WebUI durch Admin-Konto → Instanz-URL → Git-Plattform (manuell: GitHub-PAT / GitLab) → AI-Provider mit Verbindungstest → Zugriffsmodell → Übernehmen & Neustart.

**Architecture:** `SetupStatus` (Infrastructure) prüft die effektive Config auf das Pflichtset; Program.cs überspringt bei `SetupRequired` Probe + Review-Registrierungen + Webhooks (UI-Basis bleibt gemappt). Der Wizard-Fortschritt liegt als DP-verschlüsselter JSON-Blob in der vorhandenen `SetupDrafts`-Tabelle (`SetupDraftService`). `/api/setup/*` (Web) bedient den React-Wizard; „Apply" validiert per `SetupStatus` über (Draft + Env), schreibt transaktional via `SettingsService` und löst den `IAppRestarter` aus. Das SPA bekommt ein `SetupGate` vor dem `AuthGate`.

**Tech Stack:** .NET 10 Minimal API, EF Core (SQLite/Postgres), ASP.NET Data Protection, MEAI `IChatClient`, React + TS + Tailwind 4 (vorhandene UI-Komponenten `Button`/`Panel`/`Pill`), xUnit + `WebApplicationFactory`.

**Basis:** Branch baut auf `feat/setup-wizard` (PR #42, Fundament) auf. Spec: `docs/superpowers/specs/2026-07-08-setup-wizard-design.md`, Abschnitte „Setup-Modus & Wizard", „Fehlerbehandlung", „Tests", PR-Punkt 2.

## Global Constraints

- Solution-Datei ist `Naudit.slnx` — **nie** `Naudit.sln` (MSB1009).
- Code-Kommentare **Deutsch**; UI-Texte, README und `docs/` **Englisch**; Commit-Messages Deutsch mit ae/oe/ue statt Umlauten.
- Core-Regel: `Naudit.Core` kennt nur MEAI-Abstraktionen. **Dieser PR fasst Core nicht an** — alles Neue liegt in Infrastructure (`Setup/`), Web (`Endpoints/`) und `src/frontend/`.
- **Keine neuen NuGet-/npm-Pakete.** Keine neue EF-Migration (die `SetupDrafts`-Tabelle existiert seit PR 1).
- Leitprinzip der Modus-Wahl: **fehlende** Pflicht-Werte ⇒ Setup-Modus (Wizard); **ungültige** Werte (z. B. kaputter Enum) ⇒ Recovery-Modus (Probe wirft). `SetupStatus` behandelt un-parsebare Enums deshalb als „keine Aussage" und meldet dafür keine fehlenden Keys.
- Secrets sind **write-only**: keine API-Antwort enthält je einen gespeicherten Secret-Wert (Ausnahme: das von Naudit selbst **generierte** `WebhookSecret` — es muss für die manuelle Webhook-Anlage kopierbar sein und ist per Design sichtbar). Draft-Blob at rest DP-verschlüsselt, Purpose exakt `"Naudit.SetupDraft"`.
- Pflichtset (Erkennung UND Apply-Validierung, Werte per `string.IsNullOrWhiteSpace` geprüft):
  - GitLab: `Naudit:GitLab:BaseUrl`, `Naudit:GitLab:Token`, `Naudit:GitLab:WebhookSecret`
  - GitHub (Auth=Pat, Default): `Naudit:GitHub:Token`, `Naudit:GitHub:WebhookSecret`
  - GitHub (Auth=App): `Naudit:GitHub:App:AppId`, `Naudit:GitHub:App:PrivateKey`, `Naudit:GitHub:WebhookSecret`
  - AI: `Naudit:Ai:Model` (außer Provider=ClaudeCode — der Client defaultet auf „sonnet"); `Naudit:Ai:ApiKey` bei Anthropic/OpenAICompatible. **`Naudit:Ai:Endpoint` ist nie Pflicht** (AiClientFactory hat funktionierende Defaults: Ollama `http://localhost:11434`, OpenAICompatible `api.openai.com`).
- Spec-Präzisierungen (bewusste Abweichungen vom Spec-Wortlaut, Begründung = Spec-Satz „Env-komplette Deployments sehen den Wizard nie"; werden in Task 11 in die Spec zurückgeschrieben):
  1. `Ai:Endpoint` und `Ai:Model` bei ClaudeCode sind nicht Teil des Pflichtsets (s. o.).
  2. Im Setup-Modus bleibt die **gesamte UI-Basis** gemappt (Auth, `/api/me`, Accounts, Settings) — nötig für den Login-Pfad des Wizards und als Reparatur-Ausweg (z. B. GitHub-App-Werte über die Settings-Seite nachtragen, die der PR-2-Wizard noch nicht anbietet). Nur Webhooks + `POST /review` + Review-Pipeline sind aus.
  3. Der Wizard-Plattform-Schritt bietet in PR 2 **GitHub (PAT)** und **GitLab (manuelle Webhook-Anlage)** an; GitHub App (Manifest-Flow) kommt in PR 3.
- Bestehende Tests bleiben grün: `dotnet test Naudit.slnx` (Stand: 267 Tests; einzige erlaubte Anpassungen sind die in Task 3 benannten). Frontend-Gate: `cd src/frontend && npm run lint && npm run build`.
- WAF-Testfakt: `builder.UseSetting(...)`-Werte liegen **über** der DB-Quelle (zählen für `EnvOverrides` als env-gesetzt) und `UseSetting(key, "")` zählt für `SetupStatus` als **fehlend** — so erzwingen Tests den Setup-Modus.

---

### Task 1: SetupStatus — Pflichtset-Erkennung

**Files:**
- Create: `src/Naudit.Infrastructure/Setup/SetupStatus.cs`
- Test: `tests/Naudit.Tests/SetupStatusTests.cs`

**Interfaces:**
- Consumes: `IConfiguration`, `GitPlatformKind`, `GitHubAuthKind`, `AiProvider` (alle vorhanden).
- Produces: `SetupStatusResult(bool SetupRequired, IReadOnlyList<string> MissingKeys)` und `SetupStatus.Check(IConfiguration)` — von Task 3 (Program.cs) und Task 7 (Apply-Validierung) verwendet.

- [ ] **Step 1: Failing Tests schreiben**

```csharp
// tests/Naudit.Tests/SetupStatusTests.cs
using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Setup;
using Xunit;

namespace Naudit.Tests;

/// <summary>Pflichtset-Logik je Plattform/Provider. Leitprinzip: fehlend ⇒ Setup-Modus,
/// un-parsebare Enums ⇒ keine Aussage (das fängt der Recovery-Probe).</summary>
public sealed class SetupStatusTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    [Fact]
    public void LeereConfig_verlangtGitLabDefaults()
    {
        var result = SetupStatus.Check(Config());
        Assert.True(result.SetupRequired);
        Assert.Contains("Naudit:GitLab:BaseUrl", result.MissingKeys);
        Assert.Contains("Naudit:GitLab:Token", result.MissingKeys);
        Assert.Contains("Naudit:GitLab:WebhookSecret", result.MissingKeys);
        Assert.Contains("Naudit:Ai:Model", result.MissingKeys);
    }

    [Fact]
    public void GitLabKomplett_istKeinSetupFall()
    {
        var result = SetupStatus.Check(Config(
            ("Naudit:GitLab:BaseUrl", "https://gitlab.example.com"),
            ("Naudit:GitLab:Token", "t"),
            ("Naudit:GitLab:WebhookSecret", "s"),
            ("Naudit:Ai:Model", "llama3.1")));
        Assert.False(result.SetupRequired);
        Assert.Empty(result.MissingKeys);
    }

    [Fact]
    public void GitHubPat_verlangtTokenUndSecret()
    {
        var result = SetupStatus.Check(Config(
            ("Naudit:Git:Platform", "GitHub"),
            ("Naudit:Ai:Model", "m")));
        Assert.Contains("Naudit:GitHub:Token", result.MissingKeys);
        Assert.Contains("Naudit:GitHub:WebhookSecret", result.MissingKeys);
        Assert.DoesNotContain("Naudit:GitLab:Token", result.MissingKeys);
    }

    [Fact]
    public void GitHubApp_verlangtAppIdUndPrivateKey_stattToken()
    {
        var result = SetupStatus.Check(Config(
            ("Naudit:Git:Platform", "GitHub"),
            ("Naudit:GitHub:Auth", "App"),
            ("Naudit:GitHub:WebhookSecret", "s"),
            ("Naudit:Ai:Model", "m")));
        Assert.Contains("Naudit:GitHub:App:AppId", result.MissingKeys);
        Assert.Contains("Naudit:GitHub:App:PrivateKey", result.MissingKeys);
        Assert.DoesNotContain("Naudit:GitHub:Token", result.MissingKeys);
    }

    [Fact]
    public void GitHubAppKomplett_istKeinSetupFall()
    {
        var result = SetupStatus.Check(Config(
            ("Naudit:Git:Platform", "GitHub"),
            ("Naudit:GitHub:Auth", "App"),
            ("Naudit:GitHub:App:AppId", "123"),
            ("Naudit:GitHub:App:PrivateKey", "PEM"),
            ("Naudit:GitHub:WebhookSecret", "s"),
            ("Naudit:Ai:Model", "m")));
        Assert.False(result.SetupRequired);
    }

    [Fact]
    public void AnthropicOhneApiKey_fehlt_OllamaOhneEndpointNicht()
    {
        var anthropic = SetupStatus.Check(Config(
            ("Naudit:GitLab:BaseUrl", "b"), ("Naudit:GitLab:Token", "t"), ("Naudit:GitLab:WebhookSecret", "s"),
            ("Naudit:Ai:Provider", "Anthropic"), ("Naudit:Ai:Model", "m")));
        Assert.Contains("Naudit:Ai:ApiKey", anthropic.MissingKeys);

        // Ollama: Endpoint hat einen funktionierenden Default (localhost:11434) — nie Pflicht.
        var ollama = SetupStatus.Check(Config(
            ("Naudit:GitLab:BaseUrl", "b"), ("Naudit:GitLab:Token", "t"), ("Naudit:GitLab:WebhookSecret", "s"),
            ("Naudit:Ai:Provider", "Ollama"), ("Naudit:Ai:Model", "m")));
        Assert.False(ollama.SetupRequired);
    }

    [Fact]
    public void ClaudeCode_brauchtKeinModel()
    {
        var result = SetupStatus.Check(Config(
            ("Naudit:GitLab:BaseUrl", "b"), ("Naudit:GitLab:Token", "t"), ("Naudit:GitLab:WebhookSecret", "s"),
            ("Naudit:Ai:Provider", "ClaudeCode")));
        Assert.False(result.SetupRequired);
    }

    [Fact]
    public void UngueltigeEnums_ergebenKeineFehlendenKeys()
    {
        // Kaputte Enum-Werte sind ein Fall fuer den Recovery-Modus (Probe wirft), nicht fuer den Wizard.
        var result = SetupStatus.Check(Config(
            ("Naudit:Git:Platform", "Bogus"),
            ("Naudit:Ai:Provider", "Bogus")));
        Assert.False(result.SetupRequired);
    }
}
```

- [ ] **Step 2: Tests laufen lassen — müssen fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupStatusTests`
Expected: FAIL (Compile-Fehler: `Naudit.Infrastructure.Setup` existiert nicht)

- [ ] **Step 3: Implementierung**

```csharp
// src/Naudit.Infrastructure/Setup/SetupStatus.cs
using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;

namespace Naudit.Infrastructure.Setup;

/// <summary>Ergebnis der Pflichtset-Prüfung: fehlt etwas, fährt der Host im Setup-Modus
/// hoch (Wizard statt Webhooks). Wird als Singleton registriert (Status-Endpoint).</summary>
public sealed record SetupStatusResult(bool SetupRequired, IReadOnlyList<string> MissingKeys);

/// <summary>Prüft die effektive Config (DB + env) auf das Pflichtset je Plattform/Provider.
/// Leitprinzip: FEHLENDE Werte ⇒ Setup-Modus; UNGÜLTIGE Werte (un-parsebare Enums) ⇒ keine
/// Aussage hier — die fängt der Probe/Recovery-Modus in Program.cs. Wird auch von
/// POST /api/setup/apply zur Draft-Validierung wiederverwendet (Draft + env als Config).</summary>
public static class SetupStatus
{
    public static SetupStatusResult Check(IConfiguration config)
    {
        var missing = new List<string>();

        // Plattform: leer ⇒ Default GitLab (wie GitOptions); un-parsebar ⇒ keine Plattform-Pflichten.
        var platform = GitPlatformKind.GitLab;
        var platformKnown = TryReadEnum(config["Naudit:Git:Platform"], ref platform);
        if (platformKnown && platform == GitPlatformKind.GitLab)
        {
            Require(config, missing, "Naudit:GitLab:BaseUrl");
            Require(config, missing, "Naudit:GitLab:Token");
            Require(config, missing, "Naudit:GitLab:WebhookSecret");
        }
        else if (platformKnown)
        {
            var auth = GitHubAuthKind.Pat;
            var authKnown = TryReadEnum(config["Naudit:GitHub:Auth"], ref auth);
            if (authKnown && auth == GitHubAuthKind.App)
            {
                Require(config, missing, "Naudit:GitHub:App:AppId");
                Require(config, missing, "Naudit:GitHub:App:PrivateKey");
            }
            else if (authKnown)
            {
                Require(config, missing, "Naudit:GitHub:Token");
            }
            Require(config, missing, "Naudit:GitHub:WebhookSecret");
        }

        // AI: Model ist Pflicht (außer ClaudeCode — CLI defaultet auf "sonnet");
        // ApiKey nur bei Key-Providern. Endpoint hat überall funktionierende Defaults.
        var provider = AiProvider.Ollama;
        var providerKnown = TryReadEnum(config["Naudit:Ai:Provider"], ref provider);
        if (providerKnown)
        {
            if (provider != AiProvider.ClaudeCode)
                Require(config, missing, "Naudit:Ai:Model");
            if (provider is AiProvider.Anthropic or AiProvider.OpenAICompatible)
                Require(config, missing, "Naudit:Ai:ApiKey");
        }

        return new SetupStatusResult(missing.Count > 0, missing);
    }

    /// <summary>Leer ⇒ true (Default bleibt stehen); parsebar ⇒ true + Wert; sonst false.</summary>
    private static bool TryReadEnum<T>(string? raw, ref T value) where T : struct
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (Enum.TryParse<T>(raw, ignoreCase: true, out var parsed)) { value = parsed; return true; }
        return false;
    }

    private static void Require(IConfiguration config, List<string> missing, string key)
    {
        if (string.IsNullOrWhiteSpace(config[key])) missing.Add(key);
    }
}
```

- [ ] **Step 4: Tests laufen lassen — müssen bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupStatusTests`
Expected: PASS (8 Tests)

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Setup/SetupStatus.cs tests/Naudit.Tests/SetupStatusTests.cs
git commit -m "feat(setup): SetupStatus - Pflichtset-Erkennung je Plattform/Provider"
```

---

### Task 2: SetupDraftService — DP-verschlüsselter Wizard-Draft

**Files:**
- Create: `src/Naudit.Infrastructure/Setup/SetupDraftService.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (Registrierung in `AddNauditDatabase`)
- Test: `tests/Naudit.Tests/SetupDraftServiceTests.cs`

**Interfaces:**
- Consumes: `NauditDbContext.SetupDrafts` (`SetupDraftEntity { Id, Json, UpdatedAtUtc }`, aus PR 1), `IDataProtectionProvider`.
- Produces: `SetupDraftService` mit `Task SaveAsync(string json, CancellationToken)`, `Task<string?> LoadAsync(CancellationToken)`, `Task ClearAsync(CancellationToken)`; Konstante `ProtectorPurpose = "Naudit.SetupDraft"`. Genau **eine** Zeile mit `Id = 1`.

- [ ] **Step 1: Failing Tests schreiben** (Muster: `SettingsServiceTests` — SQLite `:memory:` + `EphemeralDataProtectionProvider`)

```csharp
// tests/Naudit.Tests/SetupDraftServiceTests.cs
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Setup;
using Xunit;

namespace Naudit.Tests;

/// <summary>Wizard-Draft: eine Zeile (Id=1), JSON-Blob DP-verschlüsselt at rest,
/// nicht entschlüsselbar (Keyring weg) ⇒ null statt Crash.</summary>
public sealed class SetupDraftServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly NauditDbContext _db;

    public SetupDraftServiceTests()
    {
        _conn.Open();
        _db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    [Fact]
    public async Task SaveLoad_roundtrip_undNieKlartextInDerZeile()
    {
        var service = new SetupDraftService(_db, new EphemeralDataProtectionProvider());
        await service.SaveAsync("""{"GitToken":"glpat-geheim"}""");
        await service.SaveAsync("""{"GitToken":"glpat-geheim-2"}"""); // Upsert, kein Duplikat

        Assert.Equal("""{"GitToken":"glpat-geheim-2"}""", await service.LoadAsync());
        var row = await _db.SetupDrafts.SingleAsync();
        Assert.Equal(1, row.Id);
        Assert.DoesNotContain("glpat-geheim", row.Json);
    }

    [Fact]
    public async Task Clear_entferntDenDraft()
    {
        var service = new SetupDraftService(_db, new EphemeralDataProtectionProvider());
        await service.SaveAsync("{}");
        await service.ClearAsync();
        Assert.Null(await service.LoadAsync());
        await service.ClearAsync(); // idempotent
    }

    [Fact]
    public async Task Load_nichtEntschluesselbar_gibtNull()
    {
        // Zwei getrennte Ephemeral-Provider = Keyring weg: Load darf nicht werfen.
        var writer = new SetupDraftService(_db, new EphemeralDataProtectionProvider());
        await writer.SaveAsync("{}");
        var reader = new SetupDraftService(_db, new EphemeralDataProtectionProvider());
        Assert.Null(await reader.LoadAsync());
    }
}
```

- [ ] **Step 2: Tests laufen lassen — müssen fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupDraftServiceTests`
Expected: FAIL (Compile-Fehler: `SetupDraftService` existiert nicht)

- [ ] **Step 3: Implementierung**

```csharp
// src/Naudit.Infrastructure/Setup/SetupDraftService.cs
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Setup;

/// <summary>Persistiert den Wizard-Zwischenstand als EINE Zeile (Id=1): JSON-Blob,
/// Data-Protection-verschlüsselt (er enthält Tokens/Keys, bevor sie echte Settings werden).
/// Nicht entschlüsselbar (Keyring weg) ⇒ null — der Wizard startet dann leer.</summary>
public sealed class SetupDraftService(NauditDbContext db, IDataProtectionProvider dataProtection)
{
    public const string ProtectorPurpose = "Naudit.SetupDraft";
    private const int DraftId = 1;

    public async Task SaveAsync(string json, CancellationToken ct = default)
    {
        var stored = dataProtection.CreateProtector(ProtectorPurpose).Protect(json);
        var row = await db.SetupDrafts.SingleOrDefaultAsync(d => d.Id == DraftId, ct);
        if (row is null)
            db.SetupDrafts.Add(new SetupDraftEntity { Id = DraftId, Json = stored, UpdatedAtUtc = DateTime.UtcNow });
        else
        {
            row.Json = stored;
            row.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> LoadAsync(CancellationToken ct = default)
    {
        var row = await db.SetupDrafts.SingleOrDefaultAsync(d => d.Id == DraftId, ct);
        if (row is null) return null;
        try
        {
            return dataProtection.CreateProtector(ProtectorPurpose).Unprotect(row.Json);
        }
        catch (CryptographicException)
        {
            return null; // Keyring weg ⇒ Draft gilt als nicht vorhanden, kein Crash.
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        var row = await db.SetupDrafts.SingleOrDefaultAsync(d => d.Id == DraftId, ct);
        if (row is null) return;
        db.SetupDrafts.Remove(row);
        await db.SaveChangesAsync(ct);
    }
}
```

In `src/Naudit.Infrastructure/DependencyInjection.cs`, Methode `AddNauditDatabase`, nach `services.AddScoped<AccountService>();` einfügen (der Wizard läuft im Setup-Modus, wo `AddNauditInfrastructure` nicht läuft — deshalb gehört der Service in die immer-an DB-Basis):

```csharp
        services.AddScoped<Setup.SetupDraftService>();
```

- [ ] **Step 4: Tests laufen lassen — müssen bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupDraftServiceTests`
Expected: PASS (3 Tests)

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Setup/SetupDraftService.cs src/Naudit.Infrastructure/DependencyInjection.cs tests/Naudit.Tests/SetupDraftServiceTests.cs
git commit -m "feat(setup): SetupDraftService - DP-verschluesselter Wizard-Draft (eine Zeile, Id=1)"
```

---

### Task 3: Setup-Modus im Host

Der heikelste Task: `Program.cs` bekommt die Setup-Erkennung, und die WAF-Test-Baseline ändert sich. **Hintergrund:** `appsettings.json` liefert bereits `Naudit:Ai:Model=llama3.1` und `Naudit:GitLab:BaseUrl=https://gitlab.example.com` — die Erkennung hängt in der Praxis an Token/WebhookSecret. Die bestehenden WAF-Tests setzen aber nur `Platform` + `WebhookSecret`; ohne Baseline-Tokens fielen sie alle in den Setup-Modus (Webhooks weg ⇒ rot).

**Files:**
- Modify: `src/Naudit.Web/Program.cs`
- Modify: `tests/Naudit.Tests/Fakes/TestAppFactory.cs`
- Modify: `tests/Naudit.Tests/RecoveryModeTests.cs`
- Test: `tests/Naudit.Tests/SetupModeTests.cs` (neu)

**Interfaces:**
- Consumes: `SetupStatus.Check` (Task 1).
- Produces: `SetupStatusResult` als DI-Singleton; Host-Verhalten „Setup-Modus = Review-Fläche aus, UI-Basis an" — Grundlage für Task 4–7.

- [ ] **Step 1: Failing Test schreiben**

```csharp
// tests/Naudit.Tests/SetupModeTests.cs
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>Fehlende Pflicht-Config ⇒ Setup-Modus: Health + UI-Basis laufen,
/// Webhooks/Review sind nicht gemappt (405 = nur das GET-only SPA-Fallback trifft).</summary>
public class SetupModeTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public SetupModeTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task FehlendePflichtConfig_startetSetupModus_ohneWebhooks()
    {
        // Baseline der TestAppFactory gezielt leeren: "" zaehlt fuer SetupStatus als fehlend.
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:GitLab:Token", "");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "");
        }).CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/webhook/gitlab", null)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/webhook/github", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/accounts")).StatusCode); // UI-Basis lebt
    }

    [Fact]
    public async Task KompletteConfig_laesstWebhookGemappt()
    {
        // Baseline unveraendert = konfiguriert: der GitLab-Webhook existiert (401 wegen fehlendem Token-Header).
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/webhook/gitlab", null)).StatusCode);
    }
}
```

- [ ] **Step 2: TestAppFactory-Baseline ergänzen**

`tests/Naudit.Tests/Fakes/TestAppFactory.cs`, `ConfigureWebHost` erweitern (nach der ConnectionString-Zeile):

```csharp
        // Baseline: Minimal-Config, mit der SetupStatus BEIDE Plattformen als "konfiguriert" sieht —
        // sonst starteten alle bestehenden WAF-Tests im Setup-Modus und die Webhook-Endpoints fehlten.
        // (appsettings.json liefert Ai:Model und GitLab:BaseUrl bereits.) Einzelne Tests
        // ueberschreiben gezielt per UseSetting; UseSetting(key, "") macht einen Key wieder "fehlend".
        builder.UseSetting("Naudit:GitLab:Token", "test-token");
        builder.UseSetting("Naudit:GitLab:WebhookSecret", "s");
        builder.UseSetting("Naudit:GitHub:Token", "test-token");
        builder.UseSetting("Naudit:GitHub:WebhookSecret", "s");
```

**Wichtig — bewusst NICHT in die Baseline:** `Naudit:Ai:Model` (via `UseSetting` wäre der Key env-locked und `SettingsEndpointTests.Put_speichertWert…` bräche mit 400) und `Naudit:GitHub:App:*`.

- [ ] **Step 3: Tests laufen lassen — Setup-Modus-Test muss fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupModeTests`
Expected: `FehlendePflichtConfig…` FAIL (Webhook antwortet 401 statt 405 — Setup-Modus existiert noch nicht); `KompletteConfig…` PASS

- [ ] **Step 4: Program.cs umbauen**

In `src/Naudit.Web/Program.cs`:

(a) Nach dem Bootstrap-Block (`var envOverrides = NauditConfig.InsertDbSettings(...)`) die Setup-Erkennung einfügen und den Probe-Block so ändern, dass er nur bei vollständiger Config läuft:

```csharp
    // 1b) Setup-Erkennung: fehlt Pflicht-Config (Token/Secrets/Model je Plattform & Provider),
    //     faehrt der Host im Setup-Modus hoch — Wizard statt Webhooks. FEHLENDE Werte ⇒ Wizard;
    //     UNGUELTIGE Werte (z. B. kaputter Enum) laufen weiter in den Probe ⇒ Recovery-Modus.
    var setup = Naudit.Infrastructure.Setup.SetupStatus.Check(builder.Configuration);

    // 2) Probe: registriert die Review-Infrastruktur in einen WEGWERF-Container. Wirft sie
    //    (z. B. Auth=App ohne PrivateKey), starten wir im Recovery-Modus statt in der Crash-Loop.
    //    Im Setup-Modus entfaellt der Probe — unvollstaendige Config ist dort der Normalfall.
    Exception? configError = null;
    if (!setup.SetupRequired)
    {
        try
        {
            var probe = new ServiceCollection();
            probe.AddLogging();
            probe.AddNauditInfrastructure(builder.Configuration);
        }
        catch (Exception ex) { configError = ex; }
    }
    var reviewActive = !setup.SetupRequired && configError is null;
```

(b) Im „Basis immer"-Block `builder.Services.AddSingleton(setup);` direkt nach `builder.Services.AddSingleton(new StartupState(...));` einfügen, und die Bedingung des Review-Blocks von `if (configError is null)` auf `if (reviewActive)` ändern:

```csharp
    builder.Services.AddSingleton(setup);
```

```csharp
    if (reviewActive)
    {
        builder.Services.AddNauditInfrastructure(builder.Configuration);
        builder.Services.AddSingleton<IReviewQueue, ReviewQueue>();
        builder.Services.AddHostedService<ReviewBackgroundService>();
    }
```

(c) Beim Endpoint-Mapping die Bedingung `if (configError is null)` ebenfalls auf `if (reviewActive)` ändern und den `else`-Zweig dreiteilig machen:

```csharp
    if (reviewActive)
    {
        // ... bestehender Webhook- und /review-Block unveraendert ...
    }
    else if (setup.SetupRequired)
    {
        app.Logger.LogWarning("Setup-Modus: fehlende Pflicht-Konfiguration ({Missing}) — " +
            "Webhooks/Review sind deaktiviert, Einrichtung ueber den Wizard in der WebUI.",
            string.Join(", ", setup.MissingKeys));
    }
    else
    {
        app.Logger.LogError("Recovery-Modus: {Error} — Webhooks/Review sind deaktiviert, " +
            "Korrektur ueber die Settings-Seite, dann Neustart.", configError!.Message);
    }
```

- [ ] **Step 5: RecoveryModeTests auf „ungültig statt fehlend" umstellen**

Der bisherige Trigger (GitHub App ohne Keys) ist jetzt korrekt ein **Setup**-Fall. Recovery wird über einen un-parsebaren Enum provoziert (Config vollständig per Baseline, aber `Get<GitOptions>()` wirft im Probe). In `tests/Naudit.Tests/RecoveryModeTests.cs` den `WithWebHostBuilder`-Block und den Klassen-Doc-Kommentar ersetzen:

```csharp
/// <summary>UNGUELTIGE Review-Config (kaputter Enum-Wert) ⇒ kein Crash, sondern Recovery:
/// Health + UI laufen, Webhooks/Review sind nicht gemappt. (FEHLENDE Werte sind seit dem
/// Setup-Wizard ein Setup-Fall — siehe SetupModeTests.)</summary>
```

```csharp
        var client = _factory.WithWebHostBuilder(b =>
        {
            // Baseline ist komplett (kein Setup-Fall); der kaputte Enum laesst den Probe werfen.
            b.UseSetting("Naudit:Git:Platform", "Bogus");
        }).CreateClient();
```

Die vier Asserts des Tests bleiben unverändert.

- [ ] **Step 6: Voller Testlauf**

Run: `dotnet test Naudit.slnx`
Expected: PASS — alle bisherigen Tests grün (Baseline-Verträglichkeit!) + 2 neue. Bei Rot: prüfen, ob ein Alt-Test durch die Baseline env-locked-Konflikte hat (dann hier dokumentieren und minimal-invasiv den Test-Key wechseln — nicht die Baseline aufweichen).

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Web/Program.cs tests/Naudit.Tests/Fakes/TestAppFactory.cs tests/Naudit.Tests/RecoveryModeTests.cs tests/Naudit.Tests/SetupModeTests.cs
git commit -m "feat(setup): Setup-Modus im Host - Review-Flaeche aus, wenn Pflicht-Config fehlt"
```

---

### Task 4: `GET /api/setup/status` + `POST /api/setup/admin`

**Files:**
- Create: `src/Naudit.Web/Endpoints/SetupEndpoints.cs`
- Modify: `src/Naudit.Web/Program.cs` (eine Zeile: `app.MapSetupEndpoints(setup);` nach `app.MapSettingsEndpoints();`)
- Test: `tests/Naudit.Tests/SetupEndpointTests.cs` (neu)

**Interfaces:**
- Consumes: `SetupStatusResult` (Task 3), `AccountService.CreateLocalAsync` (wirft `InvalidOperationException` bei Passwort < 8 Zeichen / leerem oder doppeltem Usernamen), `AuthEndpoints.BuildPrincipal`.
- Produces: `MapSetupEndpoints(this WebApplication app, SetupStatusResult setup)`; DTO `SetupAdminRequest(string Username, string Password)`. Der Status-Endpoint ist **immer** gemappt (der Wizard pollt ihn nach dem Neustart); alles Weitere nur bei `SetupRequired`. Tasks 5–7 hängen ihre Endpoints in dieselbe Datei/Gruppe.

- [ ] **Step 1: Failing Tests schreiben**

```csharp
// tests/Naudit.Tests/SetupEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>Wizard-API. BEWUSST kein IClassFixture: jeder Test bekommt seine eigene
/// TestAppFactory (frische DB) — die Asserts haengen von "existiert schon ein Admin?" ab
/// und duerfen sich ueber eine geteilte Fixture-DB nicht gegenseitig beeinflussen.</summary>
public class SetupEndpointTests
{
    /// <summary>Setup-Modus erzwingen: GitLab-Pflichtwerte der Baseline leeren.</summary>
    private static WebApplicationFactory<Program> SetupMode(TestAppFactory factory,
        Action<IWebHostBuilder>? extra = null) =>
        factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:GitLab:Token", "");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "");
            extra?.Invoke(b);
        });

    /// <summary>Legt den ersten Admin ueber die Wizard-API an (frische DB vorausgesetzt) —
    /// ab Task 5-7 der Standard-Einstieg fuer eingeloggte Wizard-Tests.</summary>
    private static async Task<HttpClient> LoggedInAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/setup/admin",
            new { username = "wizard-admin", password = "pw-123456" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return client;
    }

    [Fact]
    public async Task Status_imSetupModus_meldetRequiredUndMissing()
    {
        using var app = new TestAppFactory();
        var client = SetupMode(app).CreateClient();
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/status"));
        Assert.True(doc.RootElement.GetProperty("setupRequired").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("adminExists").GetBoolean());
        Assert.Contains(doc.RootElement.GetProperty("missing").EnumerateArray(),
            m => m.GetString() == "Naudit:GitLab:Token");
        Assert.Equal("http://localhost", doc.RootElement.GetProperty("suggestedPublicBaseUrl").GetString());
    }

    [Fact]
    public async Task Status_konfiguriert_meldetNichtRequired_undAdminApiFehlt()
    {
        using var app = new TestAppFactory();
        var client = app.CreateClient(); // Baseline = konfiguriert
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/status"));
        Assert.False(doc.RootElement.GetProperty("setupRequired").GetBoolean());
        // Wizard-API ist nicht gemappt ⇒ der /api-Fallback antwortet 404.
        var res = await client.PostAsJsonAsync("/api/setup/admin", new { username = "a", password = "pw-123456" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task AdminAnlegen_loggtEin_zweiterVersuchIst409()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));

        // Cookie-Session aktiv: ein RequireAuthorization-Endpoint antwortet nicht mehr 401.
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/setup/draft")).StatusCode);

        var second = await client.PostAsJsonAsync("/api/setup/admin", new { username = "x", password = "pw-123456" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task AdminAnlegen_beiGeseedetemAdmin_ist409()
    {
        using var app = new TestAppFactory();
        var client = SetupMode(app, b =>
        {
            b.UseSetting("Naudit:Ui:Admin:Username", "root");
            b.UseSetting("Naudit:Ui:Admin:InitialPassword", "passwort123");
        }).CreateClient();
        var res = await client.PostAsJsonAsync("/api/setup/admin", new { username = "x", password = "pw-123456" });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task AdminAnlegen_kurzesPasswort_ist400()
    {
        using var app = new TestAppFactory();
        var client = SetupMode(app).CreateClient();
        var res = await client.PostAsJsonAsync("/api/setup/admin", new { username = "kurz", password = "1234567" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task DraftApi_ohneLogin_ist401()
    {
        using var app = new TestAppFactory();
        var client = SetupMode(app).CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/setup/draft")).StatusCode);
    }
}
```

Hinweis: `AdminAnlegen_loggtEin…` und `DraftApi_ohneLogin…` referenzieren `GET /api/setup/draft`. Damit Task 4 eigenständig grün ist, mappt Step 2 den Draft-GET bereits vollständig (`DraftResponseAsync` — endgültige Logik inkl. Secret-Maskierung); Task 5 ergänzt nur PUT/DELETE.

- [ ] **Step 2: Implementierung**

```csharp
// src/Naudit.Web/Endpoints/SetupEndpoints.cs
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Setup;
using Naudit.Infrastructure.Ui;

namespace Naudit.Web.Endpoints;

/// <summary>Wizard-API. Der Status-Endpoint ist IMMER gemappt (das SPA entscheidet damit
/// Wizard vs. App und pollt ihn nach dem Uebernehmen-Neustart); alle anderen Endpoints
/// existieren nur im Setup-Modus. Schutz nach dem Grafana-Muster: Admin anlegen geht nur,
/// solange KEIN Admin existiert — danach ist Login Pflicht.</summary>
public static class SetupEndpoints
{
    public static void MapSetupEndpoints(this WebApplication app, SetupStatusResult setup)
    {
        app.MapGet("/api/setup/status", async (HttpContext ctx, NauditDbContext db) =>
        {
            var adminExists = await db.Accounts.AnyAsync(a => a.IsAdmin, ctx.RequestAborted);
            return Results.Ok(new
            {
                setupRequired = setup.SetupRequired,
                adminExists,
                missing = setup.MissingKeys,
                // Aus dem Request abgeleitet (ForwardedHeaders sind bereits verarbeitet) — Vorbelegung fuer Schritt 2.
                suggestedPublicBaseUrl = setup.SetupRequired ? $"{ctx.Request.Scheme}://{ctx.Request.Host}" : null,
            });
        });

        if (!setup.SetupRequired) return; // konfiguriert ⇒ keine Wizard-Flaeche

        app.MapPost("/api/setup/admin", async (SetupAdminRequest body, AccountService accounts,
            NauditDbContext db, HttpContext ctx) =>
        {
            if (await db.Accounts.AnyAsync(a => a.IsAdmin, ctx.RequestAborted))
                return Results.Conflict(new { error = "An admin account already exists — sign in instead." });
            AccountEntity acct;
            try
            {
                acct = await accounts.CreateLocalAsync(body.Username, body.Password, isAdmin: true, [], ctx.RequestAborted);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            // Direkt einloggen — die weiteren Wizard-Schritte sind RequireAuthorization + Admin-Check.
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthEndpoints.BuildPrincipal(acct));
            return Results.Ok(new { username = acct.Username });
        });

        var group = app.MapGroup("/api/setup").RequireAuthorization();

        group.MapGet("/draft", async (HttpContext ctx, NauditDbContext db, SetupDraftService drafts) =>
            await DraftResponseAsync(ctx, db, drafts));
    }

    /// <summary>GET-Antwort des Drafts — Secrets (GitToken/AiApiKey) werden NIE zurueckgegeben,
    /// nur has*-Flags. Das selbst generierte WebhookSecret ist bewusst sichtbar (Copy-Paste
    /// in die Plattform-Oberflaeche). Von Task 5 (PUT/DELETE) mitverwendet.</summary>
    internal static async Task<IResult> DraftResponseAsync(HttpContext ctx, NauditDbContext db, SetupDraftService drafts)
    {
        if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
        var json = await drafts.LoadAsync(ctx.RequestAborted);
        var draft = json is null ? new SetupDraft() : System.Text.Json.JsonSerializer.Deserialize<SetupDraft>(json)!;
        return Results.Ok(new
        {
            draft = draft with { GitToken = null, AiApiKey = null },
            hasGitToken = !string.IsNullOrEmpty(draft.GitToken),
            hasAiApiKey = !string.IsNullOrEmpty(draft.AiApiKey),
        });
    }
}

/// <summary>Request-Body der Admin-Anlage (Wizard Schritt 1).</summary>
public sealed record SetupAdminRequest(string Username, string Password);

/// <summary>Wizard-Zwischenstand: API-Kontrakt UND (serialisiert) der DP-verschluesselte
/// DB-Blob. Alle Felder optional — der Wizard fuellt sie schrittweise. GitToken ist je nach
/// Plattform der GitHub-PAT oder der GitLab-Token (api-Scope).</summary>
public sealed record SetupDraft(
    string? PublicBaseUrl = null,
    string? Platform = null,          // "GitHub" | "GitLab"
    string? GitToken = null,          // Secret: write-only ueber die API
    string? GitLabBaseUrl = null,
    string? WebhookSecret = null,     // von Naudit generiert — bewusst sichtbar/kopierbar
    string? AiProvider = null,        // "Ollama" | "Anthropic" | "OpenAICompatible" | "ClaudeCode"
    string? AiModel = null,
    string? AiEndpoint = null,
    string? AiApiKey = null,          // Secret: write-only ueber die API
    string? AccessGateMode = null);   // "Open" | "Registered"
```

In `src/Naudit.Web/Program.cs` nach `app.MapSettingsEndpoints();` einfügen:

```csharp
    app.MapSetupEndpoints(setup);
```

- [ ] **Step 3: Tests laufen lassen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupEndpointTests`
Expected: PASS (6 Tests)

- [ ] **Step 4: Commit**

```bash
git add src/Naudit.Web/Endpoints/SetupEndpoints.cs src/Naudit.Web/Program.cs tests/Naudit.Tests/SetupEndpointTests.cs
git commit -m "feat(setup): /api/setup/status (immer) + Admin-Anlage nach Grafana-Muster"
```

---

### Task 5: Draft-API — PUT/DELETE mit Write-only-Secrets

**Files:**
- Modify: `src/Naudit.Web/Endpoints/SetupEndpoints.cs`
- Test: `tests/Naudit.Tests/SetupEndpointTests.cs` (erweitern)

**Interfaces:**
- Consumes: `SetupDraftService` (Task 2), `SetupDraft`/`DraftResponseAsync` (Task 4).
- Produces: `PUT /api/setup/draft` (Merge-Semantik: leere Secret-Felder = „gespeicherten Wert behalten", außer bei Plattformwechsel), `DELETE /api/setup/draft` (Abbruch verwirft nur den Draft). Task 7 (Apply) und Task 10 (SPA) bauen darauf.

- [ ] **Step 1: Failing Tests ergänzen** (in `SetupEndpointTests`)

```csharp
    [Fact]
    public async Task Draft_putUndGet_maskiertSecrets_behaeltSieBeimUpdate()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));
        var put = await client.PutAsJsonAsync("/api/setup/draft", new
        {
            platform = "GitHub", gitToken = "ghp-geheim", webhookSecret = "hook-1", aiProvider = "Ollama",
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/draft"));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("draft").GetProperty("gitToken").ValueKind);
        Assert.True(doc.RootElement.GetProperty("hasGitToken").GetBoolean());
        // Generiertes WebhookSecret ist bewusst sichtbar (Copy-Paste in die Plattform).
        Assert.Equal("hook-1", doc.RootElement.GetProperty("draft").GetProperty("webhookSecret").GetString());

        // Update OHNE gitToken (SPA kann den maskierten Wert nicht mitschicken) ⇒ Token bleibt.
        await client.PutAsJsonAsync("/api/setup/draft", new { platform = "GitHub", webhookSecret = "hook-1", aiModel = "m" });
        doc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/draft"));
        Assert.True(doc.RootElement.GetProperty("hasGitToken").GetBoolean());

        // Plattformwechsel ⇒ gespeicherter Token verfaellt (GitHub-PAT taugt nicht fuer GitLab).
        await client.PutAsJsonAsync("/api/setup/draft", new { platform = "GitLab" });
        doc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/draft"));
        Assert.False(doc.RootElement.GetProperty("hasGitToken").GetBoolean());
    }

    [Fact]
    public async Task Draft_delete_verwirft()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));
        await client.PutAsJsonAsync("/api/setup/draft", new { platform = "GitLab", gitToken = "glpat-x" });
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/setup/draft")).StatusCode);
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/draft"));
        Assert.False(doc.RootElement.GetProperty("hasGitToken").GetBoolean());
    }
```

Kein zusätzlicher Helper nötig: jeder Test nutzt seine eigene `TestAppFactory` (frische DB — siehe Klassen-Doc aus Task 4) und steigt über `LoggedInAsync(SetupMode(app))` ein. Keine Reihenfolge-Abhängigkeiten über eine geteilte DB.

- [ ] **Step 2: Tests laufen lassen — müssen fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupEndpointTests`
Expected: FAIL (PUT/DELETE nicht gemappt ⇒ 404)

- [ ] **Step 3: Implementierung** — in `MapSetupEndpoints` nach dem Draft-GET:

```csharp
        group.MapPut("/draft", async (SetupDraft incoming, HttpContext ctx, NauditDbContext db,
            SetupDraftService drafts) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();

            // Merge-Semantik: GET maskiert Secrets, das SPA kann sie nicht zurueckschicken —
            // leere Secret-Felder heissen "gespeicherten Wert behalten". Ausnahme Plattformwechsel:
            // ein GitHub-PAT taugt nicht fuer GitLab (und umgekehrt) ⇒ Token verfaellt.
            var existingJson = await drafts.LoadAsync(ctx.RequestAborted);
            var existing = existingJson is null
                ? new SetupDraft()
                : System.Text.Json.JsonSerializer.Deserialize<SetupDraft>(existingJson)!;
            var samePlatform = string.Equals(incoming.Platform, existing.Platform, StringComparison.OrdinalIgnoreCase);
            var merged = incoming with
            {
                GitToken = !string.IsNullOrEmpty(incoming.GitToken)
                    ? incoming.GitToken : (samePlatform ? existing.GitToken : null),
                AiApiKey = !string.IsNullOrEmpty(incoming.AiApiKey) ? incoming.AiApiKey : existing.AiApiKey,
            };
            await drafts.SaveAsync(System.Text.Json.JsonSerializer.Serialize(merged), ctx.RequestAborted);
            return Results.Ok(new { saved = true });
        });

        group.MapDelete("/draft", async (HttpContext ctx, NauditDbContext db, SetupDraftService drafts) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
            await drafts.ClearAsync(ctx.RequestAborted);
            return Results.NoContent();
        });
```

- [ ] **Step 4: Tests laufen lassen — müssen bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupEndpointTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Web/Endpoints/SetupEndpoints.cs tests/Naudit.Tests/SetupEndpointTests.cs
git commit -m "feat(setup): Draft-API mit Write-only-Secrets und Merge-Semantik"
```

---

### Task 6: `POST /api/setup/test-ai` — AI-Verbindungstest

**Files:**
- Modify: `src/Naudit.Web/Endpoints/SetupEndpoints.cs`
- Modify: `src/Naudit.Web/Program.cs` (Registrierung der Factory-Seam in der immer-an-Basis)
- Test: `tests/Naudit.Tests/SetupEndpointTests.cs` (erweitern)

**Interfaces:**
- Consumes: `AiClientFactory.Create(AiOptions)`, `Fakes/FakeChatClient` (vorhanden: `internal sealed class FakeChatClient(string responseText) : IChatClient`), `SetupDraftService` (ApiKey-Fallback aus dem Draft).
- Produces: `AiTestClientFactory(Func<AiOptions, IChatClient> Create)` (DI-Seam, damit Tests ohne Netz bleiben — Testansatz des Repos), Endpoint-Antwort immer 200 mit `{ ok: bool, detail: string }` (Scheitern ist ein Ergebnis, kein 500; Spec: Fortfahren trotz Fehlschlags erlaubt). Wird von Task 10 (SPA „Test connection") gerufen; die Settings-Seiten-Variante („Verbindung testen" auch dort) ist bewusst PR-3/Follow-up.

- [ ] **Step 1: Failing Tests ergänzen** (in `SetupEndpointTests`)

```csharp
    [Fact]
    public async Task TestAi_erfolg_liefertOkTrue()
    {
        using var app = new TestAppFactory();
        var factory = SetupMode(app, b => b.ConfigureServices(s =>
            s.AddSingleton(new Naudit.Web.AiTestClientFactory(_ => new FakeChatClient("OK")))));
        var client = await LoggedInAsync(factory);
        var res = await client.PostAsJsonAsync("/api/setup/test-ai",
            new { provider = "Ollama", model = "m" });
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task TestAi_fehler_istErgebnisStattStatuscode()
    {
        using var app = new TestAppFactory();
        var factory = SetupMode(app, b => b.ConfigureServices(s =>
            s.AddSingleton(new Naudit.Web.AiTestClientFactory(_ =>
                throw new InvalidOperationException("connection refused")))));
        var client = await LoggedInAsync(factory);
        var res = await client.PostAsJsonAsync("/api/setup/test-ai", new { provider = "Ollama", model = "m" });
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("connection refused", doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task TestAi_unbekannterProvider_ist400()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));
        var res = await client.PostAsJsonAsync("/api/setup/test-ai", new { provider = "Skynet", model = "m" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
```

Benötigte Usings in der Testdatei ergänzen: `Microsoft.Extensions.DependencyInjection`, `Naudit.Web`.

- [ ] **Step 2: Tests laufen lassen — müssen fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupEndpointTests`
Expected: FAIL (`AiTestClientFactory` existiert nicht / Endpoint 404)

- [ ] **Step 3: Implementierung**

(a) Seam-Record — in `src/Naudit.Web/Endpoints/SetupEndpoints.cs` (Namespace-Ebene, neben `SetupAdminRequest`; Namespace `Naudit.Web` wählen, indem der Record stattdessen in `src/Naudit.Web/AppRestarter.cs`-Nachbarschaft… **nein**: eigene Datei):

```csharp
// src/Naudit.Web/AiTestClientFactory.cs
using Microsoft.Extensions.AI;
using Naudit.Infrastructure.Ai;

namespace Naudit.Web;

/// <summary>Seam fuer den AI-Verbindungstest: Produktion = AiClientFactory.Create,
/// Tests ersetzen die Funktion per DI (kein Netz — Testansatz des Repos).</summary>
public sealed record AiTestClientFactory(Func<AiOptions, IChatClient> Create);
```

(b) Registrierung in `src/Naudit.Web/Program.cs`, „Basis immer"-Block (nach `builder.Services.AddSingleton(setup);`):

```csharp
    builder.Services.AddSingleton(new AiTestClientFactory(Naudit.Infrastructure.Ai.AiClientFactory.Create));
```

(c) Endpoint — in `MapSetupEndpoints` nach dem Draft-DELETE:

```csharp
        group.MapPost("/test-ai", async (AiTestRequest body, HttpContext ctx, NauditDbContext db,
            SetupDraftService drafts, AiTestClientFactory factory) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
            if (!Enum.TryParse<Naudit.Infrastructure.Ai.AiProvider>(body.Provider, true, out var provider))
                return Results.BadRequest(new { error = $"Unknown AI provider '{body.Provider}'." });

            // ApiKey ist im SPA maskiert, wenn er aus dem Draft stammt — leer ⇒ Draft-Wert nehmen.
            var apiKey = body.ApiKey;
            if (string.IsNullOrEmpty(apiKey))
            {
                var json = await drafts.LoadAsync(ctx.RequestAborted);
                apiKey = json is null ? null
                    : System.Text.Json.JsonSerializer.Deserialize<SetupDraft>(json)!.AiApiKey;
            }

            var options = new Naudit.Infrastructure.Ai.AiOptions
            {
                Provider = provider,
                Model = body.Model ?? "",
                Endpoint = string.IsNullOrWhiteSpace(body.Endpoint) ? null : body.Endpoint,
                ApiKey = apiKey,
                TimeoutSeconds = 30, // Verbindungstest, kein Review — kurz halten
            };
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                var client = factory.Create(options);
                try
                {
                    var response = await client.GetResponseAsync("Reply with the single word: OK", cancellationToken: cts.Token);
                    var text = response.Text;
                    return Results.Ok(new { ok = true, detail = text.Length > 200 ? text[..200] : text });
                }
                finally
                {
                    (client as IDisposable)?.Dispose();
                }
            }
            catch (Exception ex)
            {
                // Scheitern ist hier ein ERGEBNIS (Spec: Fortfahren erlaubt, z. B. Ollama noch nicht erreichbar).
                return Results.Ok(new { ok = false, detail = ex.Message });
            }
        });
```

Und den Request-Record neben `SetupAdminRequest`:

```csharp
/// <summary>Request-Body des AI-Verbindungstests; ApiKey leer ⇒ gespeicherter Draft-Wert.</summary>
public sealed record AiTestRequest(string? Provider, string? Model, string? Endpoint, string? ApiKey);
```

Using in `SetupEndpoints.cs` ergänzen: `using Microsoft.Extensions.AI;` (für `GetResponseAsync`).

- [ ] **Step 4: Tests laufen lassen — müssen bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupEndpointTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Web/AiTestClientFactory.cs src/Naudit.Web/Endpoints/SetupEndpoints.cs src/Naudit.Web/Program.cs tests/Naudit.Tests/SetupEndpointTests.cs
git commit -m "feat(setup): AI-Verbindungstest im Wizard (Seam statt Netz in Tests)"
```

---

### Task 7: `POST /api/setup/apply` — Draft atomar übernehmen + Neustart

**Files:**
- Modify: `src/Naudit.Web/Endpoints/SetupEndpoints.cs`
- Test: `tests/Naudit.Tests/SetupEndpointTests.cs` (erweitern)

**Interfaces:**
- Consumes: `SetupStatus.Check` (Wiederverwendung als Apply-Validierung über Draft+Env), `SettingsService.SetAsync`, `SetupDraftService`, `EnvOverrides`, `IAppRestarter.RequestRestart`.
- Produces: `POST /api/setup/apply` → 400 (`{ error, missing }`) bei unvollständigem Draft; sonst transaktionales Schreiben in `Settings`, Draft-Löschung, `RequestRestart()`, 200 `{ restarting: true }`. Env-gesetzte Keys werden nicht geschrieben (env gewinnt ohnehin), zählen aber als erfüllt.

- [ ] **Step 1: Failing Tests ergänzen** (in `SetupEndpointTests`; Usings: `Microsoft.EntityFrameworkCore`, `Naudit.Infrastructure.Data`)

```csharp
    private sealed class FakeRestarter : Naudit.Web.IAppRestarter
    {
        public int RestartCalls;
        public bool RestartPending { get; private set; }
        public void RequestRestart() => RestartCalls++;
        public void MarkRestartPending() => RestartPending = true;
    }

    [Fact]
    public async Task Apply_ohneDraft_ist400()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));
        await client.DeleteAsync("/api/setup/draft");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/setup/apply", null)).StatusCode);
    }

    [Fact]
    public async Task Apply_unvollstaendig_ist400MitMissing()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));
        await client.PutAsJsonAsync("/api/setup/draft", new
        {
            publicBaseUrl = "https://naudit.example.com", platform = "GitHub", webhookSecret = "hook-1",
            aiProvider = "Ollama", aiModel = "m", // GitToken fehlt!
        });
        var res = await client.PostAsync("/api/setup/apply", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Contains(doc.RootElement.GetProperty("missing").EnumerateArray(),
            m => m.GetString() == "Naudit:GitHub:Token");
    }

    [Fact]
    public async Task Apply_vollstaendig_schreibtSettings_loeschtDraft_stoesstNeustartAn()
    {
        using var app = new TestAppFactory();
        var restarter = new FakeRestarter();
        var factory = SetupMode(app, b => b.ConfigureServices(s =>
            s.AddSingleton<Naudit.Web.IAppRestarter>(restarter)));
        var client = await LoggedInAsync(factory);
        await client.PutAsJsonAsync("/api/setup/draft", new
        {
            publicBaseUrl = "https://naudit.example.com", platform = "GitHub", gitToken = "ghp-geheim",
            webhookSecret = "hook-1", aiProvider = "Anthropic", aiModel = "claude-sonnet-5",
            aiApiKey = "sk-geheim", accessGateMode = "Registered",
        });

        var res = await client.PostAsync("/api/setup/apply", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, restarter.RestartCalls);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        var settings = await db.Settings.ToListAsync();
        Assert.Equal("GitHub", settings.Single(s => s.Key == "Naudit:Git:Platform").Value);
        Assert.Equal("Pat", settings.Single(s => s.Key == "Naudit:GitHub:Auth").Value);
        Assert.Equal("https://naudit.example.com", settings.Single(s => s.Key == "Naudit:PublicBaseUrl").Value);
        Assert.Equal("Registered", settings.Single(s => s.Key == "Naudit:AccessGate:Mode").Value);
        // Secrets liegen verschluesselt, nie im Klartext.
        Assert.DoesNotContain(settings, s => s.Value.Contains("ghp-geheim") || s.Value.Contains("sk-geheim"));
        Assert.True(settings.Single(s => s.Key == "Naudit:GitHub:Token").IsSecret);
        Assert.Empty(await db.SetupDrafts.ToListAsync()); // Draft verbraucht
        // GitLab-Keys des Drafts (leer) wurden NICHT geschrieben.
        Assert.DoesNotContain(settings, s => s.Key.StartsWith("Naudit:GitLab:"));
    }

    [Fact]
    public async Task Apply_envGesetzterKey_wirdNichtGeschrieben_aberZaehltAlsErfuellt()
    {
        // AccessGate:Mode kommt per "env" (UseSetting liegt ueber der DB-Quelle) ⇒ nicht in die DB schreiben.
        using var app = new TestAppFactory();
        var factory = SetupMode(app, b =>
        {
            b.UseSetting("Naudit:AccessGate:Mode", "Open");
            b.ConfigureServices(s => s.AddSingleton<Naudit.Web.IAppRestarter>(new FakeRestarter()));
        });
        var client = await LoggedInAsync(factory);
        await client.PutAsJsonAsync("/api/setup/draft", new
        {
            publicBaseUrl = "https://n.example.com", platform = "GitLab", gitLabBaseUrl = "https://gitlab.example.com",
            gitToken = "glpat-x", webhookSecret = "hook-2", aiProvider = "Ollama", aiModel = "m",
            accessGateMode = "Registered",
        });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/setup/apply", null)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        Assert.DoesNotContain(await db.Settings.ToListAsync(), s => s.Key == "Naudit:AccessGate:Mode");
    }
```

- [ ] **Step 2: Tests laufen lassen — müssen fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupEndpointTests`
Expected: FAIL (Apply nicht gemappt ⇒ 404)

- [ ] **Step 3: Implementierung** — in `MapSetupEndpoints` nach `test-ai`:

```csharp
        group.MapPost("/apply", async (HttpContext ctx, NauditDbContext db, SetupDraftService drafts,
            Naudit.Infrastructure.Settings.SettingsService settings,
            Naudit.Infrastructure.Settings.EnvOverrides env, IAppRestarter restarter) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
            var json = await drafts.LoadAsync(ctx.RequestAborted);
            if (json is null)
                return Results.BadRequest(new { error = "No setup draft to apply.", missing = Array.Empty<string>() });
            var draft = System.Text.Json.JsonSerializer.Deserialize<SetupDraft>(json)!;

            // Validierung = dieselbe Pflichtset-Logik wie beim Start: Draft-Werte unten,
            // env-Overrides oben (env gewinnt und zaehlt als erfuellt).
            var values = DraftToSettings(draft);
            var effective = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .AddConfiguration(env.Root)
                .Build();
            var check = SetupStatus.Check(effective);
            if (check.SetupRequired)
                return Results.BadRequest(new { error = "Setup is incomplete.", missing = check.MissingKeys });
            if (string.IsNullOrWhiteSpace(effective["Naudit:PublicBaseUrl"]))
                return Results.BadRequest(new { error = "Setup is incomplete.", missing = new[] { "Naudit:PublicBaseUrl" } });

            // Atomar uebernehmen: alle Werte + Draft-Loeschung in EINER Transaktion.
            await using var tx = await db.Database.BeginTransactionAsync(ctx.RequestAborted);
            foreach (var (key, value) in values)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;      // nicht gesetzt ⇒ nichts schreiben
                if (env.Root[key] is not null) continue;             // env gewinnt ⇒ DB nicht anfassen
                await settings.SetAsync(key, value, ctx.RequestAborted);
            }
            await drafts.ClearAsync(ctx.RequestAborted);
            await tx.CommitAsync(ctx.RequestAborted);

            restarter.RequestRestart(); // Host-Schleife baut neu — die Settings greifen danach
            return Results.Ok(new { restarting = true });
        });
```

Und die Mapping-Hilfsfunktion in der Klasse `SetupEndpoints`:

```csharp
    /// <summary>Draft → Setting-Keys. Nur die zur gewaehlten Plattform gehoerenden Keys —
    /// Reste der anderen Plattform landen nie in der DB.</summary>
    private static Dictionary<string, string?> DraftToSettings(SetupDraft d)
    {
        var values = new Dictionary<string, string?>
        {
            ["Naudit:PublicBaseUrl"] = d.PublicBaseUrl,
            ["Naudit:Git:Platform"] = d.Platform,
            ["Naudit:Ai:Provider"] = d.AiProvider,
            ["Naudit:Ai:Model"] = d.AiModel,
            ["Naudit:Ai:Endpoint"] = d.AiEndpoint,
            ["Naudit:Ai:ApiKey"] = d.AiApiKey,
            ["Naudit:AccessGate:Mode"] = d.AccessGateMode,
        };
        if (string.Equals(d.Platform, "GitHub", StringComparison.OrdinalIgnoreCase))
        {
            values["Naudit:GitHub:Auth"] = "Pat"; // PR 2 kennt nur den PAT-Pfad; App kommt mit dem Manifest-Flow (PR 3)
            values["Naudit:GitHub:Token"] = d.GitToken;
            values["Naudit:GitHub:WebhookSecret"] = d.WebhookSecret;
        }
        else if (string.Equals(d.Platform, "GitLab", StringComparison.OrdinalIgnoreCase))
        {
            values["Naudit:GitLab:BaseUrl"] = d.GitLabBaseUrl;
            values["Naudit:GitLab:Token"] = d.GitToken;
            values["Naudit:GitLab:WebhookSecret"] = d.WebhookSecret;
        }
        return values;
    }
```

Usings ergänzen: `using Microsoft.Extensions.Configuration;` — und beachten: `AddInMemoryCollection` erwartet `IEnumerable<KeyValuePair<string, string?>>` (das Dictionary passt direkt).

**Achtung Apply-Validierung GitLab:** Die appsettings-Defaults (`GitLab:BaseUrl`) sind in `effective` NICHT enthalten (nur Draft + env) — deshalb verlangt Apply für GitLab eine explizite `gitLabBaseUrl` aus dem Wizard. Das ist gewollt (der Wizard soll den Beispiel-Default nie stillschweigend übernehmen).

- [ ] **Step 4: Tests laufen lassen — müssen bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupEndpointTests`
Expected: PASS. Danach voller Lauf: `dotnet test Naudit.slnx` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Web/Endpoints/SetupEndpoints.cs tests/Naudit.Tests/SetupEndpointTests.cs
git commit -m "feat(setup): Apply - Draft atomar in Settings uebernehmen und Neustart anstossen"
```

---

### Task 8: Frontend — Typen, Shared-Bausteine, Steps 1–3

Reine Komponenten-Task: alles kompiliert eigenständig, verdrahtet wird in Task 10. UI-Texte Englisch; Stil = vorhandene Komponenten (`Button`, `Panel`, `Pill`, `inputCls`-Muster der LoginPage).

**Files:**
- Modify: `src/frontend/src/api/types.ts`
- Create: `src/frontend/src/components/setup/shared.tsx`
- Create: `src/frontend/src/components/setup/StepAdmin.tsx`
- Create: `src/frontend/src/components/setup/StepInstance.tsx`
- Create: `src/frontend/src/components/setup/StepPlatform.tsx`

**Interfaces:**
- Consumes: `api`/`ApiError` (`@/api/client`), `Button` (`@/components/ui/Button`).
- Produces: `SetupStatusDto`, `SetupDraftResponse` (types.ts); `WizardDraft`, `emptyDraft`, `inputCls`, `randomSecret`, `Field`, `CopyRow` (shared.tsx); Step-Komponenten mit den unten definierten Props — Task 10 konsumiert sie exakt so.

- [ ] **Step 1: Typen ergänzen** (`src/frontend/src/api/types.ts`, ans Ende)

```ts
export interface SetupStatusDto {
  setupRequired: boolean;
  adminExists: boolean;
  missing: string[];
  suggestedPublicBaseUrl: string | null;
}

export interface SetupDraftDto {
  publicBaseUrl: string | null;
  platform: "GitHub" | "GitLab" | null;
  gitToken: string | null; // von der API immer maskiert (null) — hasGitToken zeigt "gesetzt"
  gitLabBaseUrl: string | null;
  webhookSecret: string | null;
  aiProvider: string | null;
  aiModel: string | null;
  aiEndpoint: string | null;
  aiApiKey: string | null; // von der API immer maskiert (null) — hasAiApiKey zeigt "gesetzt"
  accessGateMode: "Open" | "Registered" | null;
}

export interface SetupDraftResponse {
  draft: SetupDraftDto;
  hasGitToken: boolean;
  hasAiApiKey: boolean;
}
```

- [ ] **Step 2: Shared-Bausteine**

```tsx
// src/frontend/src/components/setup/shared.tsx
import { useState, type ReactNode } from "react";

/** Wizard-interner Draft-State: immer Strings ("" = leer). Secrets bleiben leer, wenn der
 *  Server sie bereits hat (has*-Flags) — leer senden heisst "gespeicherten Wert behalten". */
export interface WizardDraft {
  publicBaseUrl: string;
  platform: "" | "GitHub" | "GitLab";
  gitToken: string;
  gitLabBaseUrl: string;
  webhookSecret: string;
  aiProvider: string;
  aiModel: string;
  aiEndpoint: string;
  aiApiKey: string;
  accessGateMode: "Open" | "Registered";
}

export const emptyDraft: WizardDraft = {
  publicBaseUrl: "",
  platform: "",
  gitToken: "",
  gitLabBaseUrl: "",
  webhookSecret: "",
  aiProvider: "Ollama",
  aiModel: "",
  aiEndpoint: "",
  aiApiKey: "",
  accessGateMode: "Open",
};

export const inputCls =
  "w-full rounded-lg border border-border bg-bg px-4 py-3 font-mono text-[13.5px] text-ink outline-none placeholder:text-ink3 focus:border-acc";

/** 32 Zufallsbytes als Hex — das Webhook-Secret, das der Nutzer in GitLab/GitHub eintraegt. */
export function randomSecret(): string {
  const bytes = new Uint8Array(32);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="flex flex-col gap-1.5">
      <span className="text-[12.5px] font-medium text-ink2">{label}</span>
      {children}
      {hint && <span className="text-[11.5px] text-ink3">{hint}</span>}
    </label>
  );
}

/** Kopierbarer Wert (Webhook-URL/-Secret) mit Feedback am Button. */
export function CopyRow({ label, value }: { label: string; value: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="flex items-center justify-between gap-3 rounded-lg border border-hairline bg-elev px-3 py-2">
      <div className="min-w-0">
        <div className="text-[11px] text-ink3">{label}</div>
        <div className="truncate font-mono text-[12.5px] text-ink">{value}</div>
      </div>
      <button
        type="button"
        className="shrink-0 cursor-pointer rounded border border-border px-2 py-1 font-mono text-[11px] text-ink2 hover:border-ink3"
        onClick={() => {
          void navigator.clipboard.writeText(value);
          setCopied(true);
          setTimeout(() => setCopied(false), 1500);
        }}
      >
        {copied ? "copied ✓" : "copy"}
      </button>
    </div>
  );
}
```

- [ ] **Step 3: Step 1 — Admin anlegen oder einloggen**

```tsx
// src/frontend/src/components/setup/StepAdmin.tsx
import { useState, type FormEvent } from "react";
import { api, ApiError } from "@/api/client";
import { Button } from "@/components/ui/Button";
import { Field, inputCls } from "./shared";

/** Schritt 1: Grafana-Muster — solange kein Admin existiert, wird hier einer angelegt
 *  (Server erzwingt das); existiert einer, ist Login Pflicht. Beide Wege setzen die
 *  Cookie-Session fuer die restlichen Schritte. */
export function StepAdmin({ adminExists, onDone }: { adminExists: boolean; onDone: () => void }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      if (adminExists) {
        await api("/auth/login", { method: "POST", body: JSON.stringify({ username, password }) });
      } else {
        await api("/api/setup/admin", { method: "POST", body: JSON.stringify({ username, password }) });
      }
      onDone();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) setError("Wrong username or password.");
      else if (err instanceof ApiError && err.status === 409) setError("An admin already exists — sign in instead.");
      else if (err instanceof ApiError && err.status === 400) setError("Username must not be empty; password needs at least 8 characters.");
      else setError("Request failed — try again.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="flex flex-col gap-4">
      <p className="text-[12.5px] text-ink3">
        {adminExists
          ? "An admin account already exists. Sign in to continue the setup."
          : "Create the admin account for this Naudit instance. It manages settings and account approvals."}
      </p>
      <Field label="Username">
        <input className={inputCls} value={username} onChange={(e) => setUsername(e.target.value)} />
      </Field>
      <Field label="Password" hint={adminExists ? undefined : "At least 8 characters."}>
        <input className={inputCls} type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
      </Field>
      {error && <div className="font-mono text-xs text-danger">{error}</div>}
      <Button type="submit" disabled={busy || !username || !password} className="w-full py-3">
        {adminExists ? "Sign in & continue" : "Create admin & continue"}
      </Button>
    </form>
  );
}
```

- [ ] **Step 4: Step 2 — Instanz-URL**

```tsx
// src/frontend/src/components/setup/StepInstance.tsx
import { Button } from "@/components/ui/Button";
import { Field, inputCls, type WizardDraft } from "./shared";

/** Schritt 2: oeffentliche Basis-URL (fuer Webhook-URLs; PR 3 nutzt sie auch fuer den
 *  GitHub-Manifest-Redirect). Aus dem Request vorbefuellt, editierbar. */
export function StepInstance({ draft, update, onNext }: {
  draft: WizardDraft;
  update: (patch: Partial<WizardDraft>) => void;
  onNext: () => void;
}) {
  const valid = /^https?:\/\/.+/.test(draft.publicBaseUrl);
  return (
    <div className="flex flex-col gap-4">
      <Field
        label="Public base URL"
        hint="The URL your git platform can reach Naudit at — used to build the webhook URLs."
      >
        <input
          className={inputCls}
          placeholder="https://naudit.example.com"
          value={draft.publicBaseUrl}
          onChange={(e) => update({ publicBaseUrl: e.target.value })}
        />
      </Field>
      <Button onClick={onNext} disabled={!valid} className="w-full py-3">
        Continue
      </Button>
    </div>
  );
}
```

- [ ] **Step 5: Step 3 — Git-Plattform (GitHub-PAT / GitLab, manuelle Webhook-Anlage)**

```tsx
// src/frontend/src/components/setup/StepPlatform.tsx
import { Button } from "@/components/ui/Button";
import { CopyRow, Field, inputCls, randomSecret, type WizardDraft } from "./shared";

/** Schritt 3: Plattform-Wahl. PR 2 = manuelle Pfade (GitHub-PAT, GitLab); der
 *  GitHub-App-Manifest-Flow kommt in PR 3. Das Webhook-Secret generiert Naudit —
 *  der Nutzer traegt URL + Secret in der Plattform ein (Anleitung inline). */
export function StepPlatform({ draft, hasGitToken, update, onNext }: {
  draft: WizardDraft;
  hasGitToken: boolean;
  update: (patch: Partial<WizardDraft>) => void;
  onNext: () => void;
}) {
  const base = draft.publicBaseUrl.replace(/\/+$/, "");
  const pick = (platform: "GitHub" | "GitLab") =>
    update({ platform, gitToken: "", webhookSecret: draft.webhookSecret || randomSecret() });

  const tokenOk = draft.gitToken !== "" || (hasGitToken && draft.platform !== "");
  const ready =
    draft.platform === "GitHub"
      ? tokenOk
      : draft.platform === "GitLab"
        ? tokenOk && /^https?:\/\/.+/.test(draft.gitLabBaseUrl)
        : false;

  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-2 gap-3">
        {(["GitHub", "GitLab"] as const).map((p) => (
          <button
            key={p}
            type="button"
            onClick={() => pick(p)}
            className={`cursor-pointer rounded-xl border px-4 py-3 text-left font-mono text-[13px] ${
              draft.platform === p ? "border-acc text-ink" : "border-border text-ink2 hover:border-ink3"
            }`}
          >
            <div className="font-bold">{p}</div>
            <div className="mt-1 text-[11px] text-ink3">
              {p === "GitHub" ? "Personal access token" : "Self-hosted or gitlab.com"}
            </div>
          </button>
        ))}
      </div>
      <p className="text-[11.5px] text-ink3">
        Using a GitHub App? One-click app creation is coming next — for now, configure it on the Settings page.
      </p>

      {draft.platform === "GitLab" && (
        <Field label="GitLab base URL">
          <input
            className={inputCls}
            placeholder="https://gitlab.example.com"
            value={draft.gitLabBaseUrl}
            onChange={(e) => update({ gitLabBaseUrl: e.target.value })}
          />
        </Field>
      )}
      {draft.platform !== "" && (
        <Field
          label={draft.platform === "GitHub" ? "GitHub personal access token" : "GitLab access token (api scope)"}
          hint={hasGitToken && draft.gitToken === "" ? "A token is already stored — leave empty to keep it." : undefined}
        >
          <input
            className={inputCls}
            type="password"
            placeholder={hasGitToken ? "•••••• (stored)" : ""}
            value={draft.gitToken}
            onChange={(e) => update({ gitToken: e.target.value })}
          />
        </Field>
      )}

      {draft.platform !== "" && (
        <div className="flex flex-col gap-2">
          <div className="text-[12.5px] font-medium text-ink2">
            Add this webhook in {draft.platform === "GitHub" ? "your repository settings (Webhooks → Add webhook, event: pull requests)" : "your project settings (Webhooks, trigger: merge request events)"}:
          </div>
          <CopyRow label="Webhook URL" value={`${base}/webhook/${draft.platform.toLowerCase()}`} />
          <CopyRow label={draft.platform === "GitHub" ? "Webhook secret" : "Secret token"} value={draft.webhookSecret} />
        </div>
      )}

      <Button onClick={onNext} disabled={!ready} className="w-full py-3">
        Continue
      </Button>
    </div>
  );
}
```

- [ ] **Step 6: Lint + Build**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: beide grün (die neuen Komponenten sind noch unreferenziert — das ist okay, tsc/ESLint melden keine unbenutzten Exporte).

- [ ] **Step 7: Commit**

```bash
git add src/frontend/src/api/types.ts src/frontend/src/components/setup/
git commit -m "feat(webui): Setup-Wizard Schritte Admin/Instanz-URL/Plattform (Komponenten)"
```

---

### Task 9: Frontend — Steps 4–6 (AI, Zugriffsmodell, Zusammenfassung)

**Files:**
- Create: `src/frontend/src/components/setup/StepAi.tsx`
- Create: `src/frontend/src/components/setup/StepAccess.tsx`
- Create: `src/frontend/src/components/setup/StepSummary.tsx`

**Interfaces:**
- Consumes: `shared.tsx`-Bausteine (Task 8), `api` für den Verbindungstest.
- Produces: `StepAi({ draft, hasAiApiKey, update, onNext })`, `StepAccess({ draft, update, onNext })`, `StepSummary({ draft, hasGitToken, hasAiApiKey, applying, applyError, onApply })` — von Task 10 exakt so konsumiert.

- [ ] **Step 1: Step 4 — AI-Provider mit Verbindungstest**

```tsx
// src/frontend/src/components/setup/StepAi.tsx
import { useState } from "react";
import { api } from "@/api/client";
import { Button } from "@/components/ui/Button";
import { Field, inputCls, type WizardDraft } from "./shared";

const PROVIDERS = ["Ollama", "Anthropic", "OpenAICompatible", "ClaudeCode"] as const;

/** Schritt 4: Provider + bedingte Felder. "Test connection" ist optional — ein Fehlschlag
 *  blockiert nicht (Spec: Ollama ist z. B. oft erst spaeter erreichbar). */
export function StepAi({ draft, hasAiApiKey, update, onNext }: {
  draft: WizardDraft;
  hasAiApiKey: boolean;
  update: (patch: Partial<WizardDraft>) => void;
  onNext: () => void;
}) {
  const [test, setTest] = useState<{ ok: boolean; detail: string } | null>(null);
  const [testing, setTesting] = useState(false);

  const needsKey = draft.aiProvider === "Anthropic" || draft.aiProvider === "OpenAICompatible";
  const showsEndpoint = draft.aiProvider === "Ollama" || draft.aiProvider === "OpenAICompatible";
  const keyOk = !needsKey || draft.aiApiKey !== "" || hasAiApiKey;
  const modelOk = draft.aiProvider === "ClaudeCode" || draft.aiModel !== "";
  const ready = modelOk && keyOk;

  async function runTest() {
    setTesting(true);
    setTest(null);
    try {
      const res = await api<{ ok: boolean; detail: string }>("/api/setup/test-ai", {
        method: "POST",
        body: JSON.stringify({
          provider: draft.aiProvider,
          model: draft.aiModel,
          endpoint: draft.aiEndpoint || null,
          apiKey: draft.aiApiKey || null, // leer ⇒ Server nimmt den gespeicherten Draft-Key
        }),
      });
      setTest(res);
    } catch {
      setTest({ ok: false, detail: "Request failed — is the server reachable?" });
    } finally {
      setTesting(false);
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <Field label="Provider">
        <select
          className={inputCls}
          value={draft.aiProvider}
          onChange={(e) => { update({ aiProvider: e.target.value }); setTest(null); }}
        >
          {PROVIDERS.map((p) => <option key={p} value={p}>{p}</option>)}
        </select>
      </Field>
      <Field
        label="Model"
        hint={draft.aiProvider === "ClaudeCode" ? "Optional — the CLI defaults to \"sonnet\"." : undefined}
      >
        <input
          className={inputCls}
          placeholder={draft.aiProvider === "ClaudeCode" ? "sonnet" : "e.g. claude-sonnet-5, qwen3.5"}
          value={draft.aiModel}
          onChange={(e) => update({ aiModel: e.target.value })}
        />
      </Field>
      {showsEndpoint && (
        <Field label="Endpoint" hint="Optional — defaults to a sensible endpoint for the provider.">
          <input
            className={inputCls}
            placeholder={draft.aiProvider === "Ollama" ? "http://localhost:11434" : "https://api.openai.com/v1"}
            value={draft.aiEndpoint}
            onChange={(e) => update({ aiEndpoint: e.target.value })}
          />
        </Field>
      )}
      {needsKey && (
        <Field
          label="API key"
          hint={hasAiApiKey && draft.aiApiKey === "" ? "A key is already stored — leave empty to keep it." : undefined}
        >
          <input
            className={inputCls}
            type="password"
            placeholder={hasAiApiKey ? "•••••• (stored)" : ""}
            value={draft.aiApiKey}
            onChange={(e) => update({ aiApiKey: e.target.value })}
          />
        </Field>
      )}

      <div className="flex items-center gap-3">
        <Button variant="secondary" onClick={() => void runTest()} disabled={testing || !ready} className="px-3 py-2 text-[12.5px]">
          {testing ? "testing…" : "Test connection"}
        </Button>
        {test && (
          <span className={`font-mono text-[11.5px] ${test.ok ? "text-acc" : "text-warn"}`}>
            {test.ok ? "✓ connection works" : `⚠ ${test.detail}`}
          </span>
        )}
      </div>
      {test && !test.ok && (
        <p className="text-[11.5px] text-ink3">You can continue anyway and fix the AI settings later.</p>
      )}

      <Button onClick={onNext} disabled={!ready} className="w-full py-3">
        Continue
      </Button>
    </div>
  );
}
```

- [ ] **Step 2: Step 5 — Zugriffsmodell**

```tsx
// src/frontend/src/components/setup/StepAccess.tsx
import { Button } from "@/components/ui/Button";
import type { WizardDraft } from "./shared";

const OPTIONS = [
  {
    mode: "Open" as const,
    title: "Open",
    text: "Every project with a valid webhook secret gets reviewed. Typical for a company GitLab or a private instance.",
  },
  {
    mode: "Registered" as const,
    title: "Registered",
    text: "Only projects of approved accounts get reviewed. Recommended if your instance is publicly reachable.",
  },
];

/** Schritt 5: AccessGate-Modus mit plattformabhaengiger Empfehlung. */
export function StepAccess({ draft, update, onNext }: {
  draft: WizardDraft;
  update: (patch: Partial<WizardDraft>) => void;
  onNext: () => void;
}) {
  const recommended = draft.platform === "GitHub" ? "Registered" : "Open";
  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-3">
        {OPTIONS.map((o) => (
          <button
            key={o.mode}
            type="button"
            onClick={() => update({ accessGateMode: o.mode })}
            className={`cursor-pointer rounded-xl border px-4 py-3 text-left ${
              draft.accessGateMode === o.mode ? "border-acc" : "border-border hover:border-ink3"
            }`}
          >
            <div className="font-mono text-[13px] font-bold text-ink">
              {o.title}
              {recommended === o.mode && <span className="ml-2 text-[11px] font-normal text-acc">recommended</span>}
            </div>
            <div className="mt-1 text-[11.5px] text-ink3">{o.text}</div>
          </button>
        ))}
      </div>
      <Button onClick={onNext} className="w-full py-3">
        Continue
      </Button>
    </div>
  );
}
```

- [ ] **Step 3: Step 6 — Zusammenfassung & Übernehmen**

```tsx
// src/frontend/src/components/setup/StepSummary.tsx
import { Button } from "@/components/ui/Button";
import { CopyRow, type WizardDraft } from "./shared";

/** Schritt 6: alles auf einen Blick (Secrets maskiert), Webhook-Daten nochmal prominent —
 *  nach dem Neustart sind Secrets write-only und nicht mehr ablesbar. */
export function StepSummary({ draft, hasGitToken, hasAiApiKey, applying, applyError, onApply }: {
  draft: WizardDraft;
  hasGitToken: boolean;
  hasAiApiKey: boolean;
  applying: boolean;
  applyError: string | null;
  onApply: () => void;
}) {
  const base = draft.publicBaseUrl.replace(/\/+$/, "");
  const rows: [string, string][] = [
    ["Public base URL", draft.publicBaseUrl],
    ["Platform", draft.platform],
    ["Git token", draft.gitToken !== "" || hasGitToken ? "•••••• (set)" : "—"],
    ...(draft.platform === "GitLab" ? ([["GitLab base URL", draft.gitLabBaseUrl]] as [string, string][]) : []),
    ["AI provider", draft.aiProvider],
    ["AI model", draft.aiModel || "(default)"],
    ["AI endpoint", draft.aiEndpoint || "(default)"],
    ...(draft.aiApiKey !== "" || hasAiApiKey ? ([["AI API key", "•••••• (set)"]] as [string, string][]) : []),
    ["Access mode", draft.accessGateMode],
  ];
  return (
    <div className="flex flex-col gap-4">
      <div className="overflow-hidden rounded-xl border border-hairline">
        {rows.map(([k, v]) => (
          <div key={k} className="flex items-center justify-between border-b border-hairline px-4 py-2.5 last:border-b-0">
            <span className="text-[12.5px] text-ink3">{k}</span>
            <span className="font-mono text-[12.5px] text-ink">{v}</span>
          </div>
        ))}
      </div>

      <div className="flex flex-col gap-2">
        <div className="text-[12.5px] font-medium text-ink2">
          Make sure this webhook is configured — copy it now, the secret is write-only after setup:
        </div>
        <CopyRow label="Webhook URL" value={`${base}/webhook/${draft.platform.toLowerCase()}`} />
        <CopyRow label="Webhook secret" value={draft.webhookSecret} />
      </div>

      {applyError && <div className="font-mono text-xs text-danger">{applyError}</div>}
      <Button onClick={onApply} disabled={applying} className="w-full py-3">
        {applying ? "applying & restarting…" : "Apply & restart"}
      </Button>
    </div>
  );
}
```

- [ ] **Step 4: Lint + Build**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: grün

- [ ] **Step 5: Commit**

```bash
git add src/frontend/src/components/setup/
git commit -m "feat(webui): Setup-Wizard Schritte AI/Zugriffsmodell/Zusammenfassung (Komponenten)"
```

---

### Task 10: Frontend — SetupWizard + SetupGate verdrahten

**Files:**
- Create: `src/frontend/src/components/setup/SetupWizard.tsx`
- Create: `src/frontend/src/components/setup/SetupGate.tsx`
- Modify: `src/frontend/src/App.tsx`

**Interfaces:**
- Consumes: alle Step-Komponenten (Tasks 8–9), `SetupStatusDto`/`SetupDraftResponse` (types), `api`.
- Produces: `<SetupGate>` um `<AuthGate>` in `App.tsx`; Apply-Flow mit Status-Polling über den Neustart hinweg; Done-Screen; „Open settings instead"-Bypass (Reparatur-Ausweg, wenn ein Admin existiert).

- [ ] **Step 1: SetupWizard**

```tsx
// src/frontend/src/components/setup/SetupWizard.tsx
import { useCallback, useState } from "react";
import { api } from "@/api/client";
import type { SetupDraftResponse, SetupStatusDto } from "@/api/types";
import { Button } from "@/components/ui/Button";
import { Logo } from "@/components/ui/Logo";
import { emptyDraft, type WizardDraft } from "./shared";
import { StepAdmin } from "./StepAdmin";
import { StepInstance } from "./StepInstance";
import { StepPlatform } from "./StepPlatform";
import { StepAi } from "./StepAi";
import { StepAccess } from "./StepAccess";
import { StepSummary } from "./StepSummary";

const TITLES = ["Admin account", "Instance URL", "Git platform", "AI provider", "Access model", "Review & apply"];

/** First-Run-Wizard: linearer Flow, Fortschritt liegt serverseitig als DP-verschluesselter
 *  Draft (ueberlebt Reloads und den Manifest-Redirect in PR 3). Nach "Apply" pollt der
 *  Wizard den Status ueber den In-Process-Neustart hinweg. */
export function SetupWizard({ status, onBypass }: { status: SetupStatusDto; onBypass: () => void }) {
  const [step, setStep] = useState(0);
  const [draft, setDraft] = useState<WizardDraft>({ ...emptyDraft, publicBaseUrl: status.suggestedPublicBaseUrl ?? "" });
  const [hasGitToken, setHasGitToken] = useState(false);
  const [hasAiApiKey, setHasAiApiKey] = useState(false);
  const [applying, setApplying] = useState(false);
  const [applyError, setApplyError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  const update = useCallback((patch: Partial<WizardDraft>) => setDraft((d) => ({ ...d, ...patch })), []);

  // Nach Schritt 1 (Session steht): gespeicherten Draft laden und lokalen State auffuellen.
  async function loadDraft() {
    const res = await api<SetupDraftResponse>("/api/setup/draft");
    setHasGitToken(res.hasGitToken);
    setHasAiApiKey(res.hasAiApiKey);
    setDraft((d) => ({
      ...d,
      publicBaseUrl: res.draft.publicBaseUrl ?? d.publicBaseUrl,
      platform: (res.draft.platform ?? d.platform) as WizardDraft["platform"],
      gitLabBaseUrl: res.draft.gitLabBaseUrl ?? d.gitLabBaseUrl,
      webhookSecret: res.draft.webhookSecret ?? d.webhookSecret,
      aiProvider: res.draft.aiProvider ?? d.aiProvider,
      aiModel: res.draft.aiModel ?? d.aiModel,
      aiEndpoint: res.draft.aiEndpoint ?? d.aiEndpoint,
      accessGateMode: (res.draft.accessGateMode ?? d.accessGateMode) as WizardDraft["accessGateMode"],
    }));
    setStep(1);
  }

  // Draft bei jedem Weiter speichern — leere Secrets bedeuten serverseitig "behalten".
  async function saveAndNext() {
    await api("/api/setup/draft", { method: "PUT", body: JSON.stringify(draft) });
    if (draft.gitToken !== "") setHasGitToken(true);
    if (draft.aiApiKey !== "") setHasAiApiKey(true);
    setStep((s) => s + 1);
  }

  async function apply() {
    setApplying(true);
    setApplyError(null);
    try {
      await api("/api/setup/draft", { method: "PUT", body: JSON.stringify(draft) });
      await api("/api/setup/apply", { method: "POST" });
    } catch {
      setApplyError("Apply failed — check the values (all required fields set?) and try again.");
      setApplying(false);
      return;
    }
    // Der Host startet in-process neu (~2 s): Status pollen, Fehler = "noch am Neustarten".
    const deadline = Date.now() + 90_000;
    while (Date.now() < deadline) {
      await new Promise((r) => setTimeout(r, 1500));
      try {
        const s = await api<SetupStatusDto>("/api/setup/status");
        if (!s.setupRequired) {
          setDone(true);
          setApplying(false);
          return;
        }
      } catch {
        /* Neustart laeuft noch */
      }
    }
    setApplyError("Restart timed out — reload the page and check the Settings.");
    setApplying(false);
  }

  const base = draft.publicBaseUrl.replace(/\/+$/, "");
  return (
    <div className="grid min-h-full place-items-center bg-[radial-gradient(130%_90%_at_50%_0%,rgba(74,222,128,.06),transparent_62%)] p-8">
      <div className="flex w-[560px] max-w-full flex-col">
        <div className="mb-6 flex items-center gap-3">
          <Logo size={40} />
          <div>
            <div className="font-mono text-lg font-bold text-white">naudit setup</div>
            <div className="font-mono text-[11px] text-ink3">
              {done ? "complete" : `step ${step + 1} of ${TITLES.length} · ${TITLES[step]}`}
            </div>
          </div>
        </div>

        <div className="rounded-2xl border border-hairline bg-surface p-6">
          {done ? (
            <div className="flex flex-col gap-4">
              <div className="font-mono text-[14px] font-bold text-acc">✓ Naudit is configured.</div>
              <p className="text-[12.5px] text-ink3">
                Reviews start as soon as your platform delivers webhooks to{" "}
                <span className="font-mono text-ink2">{`${base}/webhook/${draft.platform.toLowerCase()}`}</span>.
              </p>
              <Button onClick={() => window.location.reload()} className="w-full py-3">
                Open Naudit
              </Button>
            </div>
          ) : (
            <>
              {step === 0 && <StepAdmin adminExists={status.adminExists} onDone={() => void loadDraft()} />}
              {step === 1 && <StepInstance draft={draft} update={update} onNext={() => void saveAndNext()} />}
              {step === 2 && (
                <StepPlatform draft={draft} hasGitToken={hasGitToken} update={update} onNext={() => void saveAndNext()} />
              )}
              {step === 3 && (
                <StepAi draft={draft} hasAiApiKey={hasAiApiKey} update={update} onNext={() => void saveAndNext()} />
              )}
              {step === 4 && <StepAccess draft={draft} update={update} onNext={() => void saveAndNext()} />}
              {step === 5 && (
                <StepSummary
                  draft={draft}
                  hasGitToken={hasGitToken}
                  hasAiApiKey={hasAiApiKey}
                  applying={applying}
                  applyError={applyError}
                  onApply={() => void apply()}
                />
              )}
            </>
          )}
        </div>

        {!done && (
          <div className="mt-4 flex items-center justify-between font-mono text-[11.5px] text-ink3">
            {step > 1 && !applying ? (
              <button type="button" className="cursor-pointer hover:text-ink" onClick={() => setStep((s) => s - 1)}>
                ← back
              </button>
            ) : (
              <span />
            )}
            {status.adminExists && step > 0 && (
              <button type="button" className="cursor-pointer hover:text-ink" onClick={onBypass}>
                open settings instead →
              </button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
```

- [ ] **Step 2: SetupGate**

```tsx
// src/frontend/src/components/setup/SetupGate.tsx
import { useEffect, useState, type ReactNode } from "react";
import { api } from "@/api/client";
import type { SetupStatusDto } from "@/api/types";
import { SetupWizard } from "./SetupWizard";

/** Vor dem AuthGate: braucht die Instanz Setup, kommt der Wizard statt der App.
 *  Status-Fehler ⇒ normale App (das AuthGate hat eigenes Error-Handling). Bypass =
 *  Reparatur-Ausweg fuer Admins (Settings-Seite statt Wizard). */
export function SetupGate({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<SetupStatusDto | null | "error">(null);
  const [bypass, setBypass] = useState(false);

  useEffect(() => {
    api<SetupStatusDto>("/api/setup/status").then(setStatus, () => setStatus("error"));
  }, []);

  if (status === null) {
    return <div className="grid h-full place-items-center font-mono text-ink3">loading…</div>;
  }
  if (status !== "error" && status.setupRequired && !bypass) {
    return <SetupWizard status={status} onBypass={() => setBypass(true)} />;
  }
  return <>{children}</>;
}
```

- [ ] **Step 3: App.tsx einhängen** — Import ergänzen und `App` ändern:

```tsx
import { SetupGate } from "@/components/setup/SetupGate";
```

```tsx
export default function App() {
  return (
    <SetupGate>
      <AuthGate>
        <Shell />
      </AuthGate>
    </SetupGate>
  );
}
```

- [ ] **Step 4: Lint + Build + voller Backend-Lauf**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: grün
Run: `dotnet test Naudit.slnx`
Expected: PASS (Frontend-Änderungen berühren das Backend nicht — Kontrolle gegen versehentliche Backend-Edits)

- [ ] **Step 5: Commit**

```bash
git add src/frontend/src/components/setup/ src/frontend/src/App.tsx
git commit -m "feat(webui): Setup-Wizard verdrahtet - SetupGate, Apply-Flow mit Neustart-Polling"
```

---

### Task 11: Docs, Spec-Präzisierung, manueller Smoke-Test

**Files:**
- Modify: `docs/getting-started.md` (Wizard-Pfad als primärer Quickstart: `docker run -p 8080:8080 -v naudit-data:/data ghcr.io/benediktnau/naudit` → Browser → Wizard; env-Pfad bleibt als Alternative)
- Modify: `docs/configuration.md` (Abschnitt „Setup mode": Pflichtset-Tabelle aus den Global Constraints dieses Plans; `Naudit:PublicBaseUrl` ist **nicht mehr** „reserved" — er wird vom Wizard gesetzt und für die angezeigten Webhook-URLs verwendet)
- Modify: `docs/webui.md` (Wizard-Abschnitt: Schritte, Grafana-Schutzmuster, Draft-Persistenz DP-verschlüsselt, „missing ⇒ wizard / invalid ⇒ recovery")
- Modify: `CLAUDE.md` (im „DB + WebUI"-Block: Satz „a first-run setup wizard … are a separate, not-yet-built follow-up PR" ersetzen durch: Wizard ist gebaut (`SetupStatus`, Setup-Modus, `/api/setup/*`, `SetupDraftService`, SPA-`SetupGate`); nur die Plattform-Automation (PR 3) steht aus)
- Modify: `docs/superpowers/specs/2026-07-08-setup-wizard-design.md` (die drei Präzisierungen aus den Global Constraints einarbeiten: Pflichtset-Fußnote zu Endpoint/ClaudeCode-Model; UI-Basis bleibt im Setup-Modus gemappt; PR-2-Plattform-Schritt = PAT/GitLab)

**Schreibregeln:** Docs Englisch, knapp, bestehende Struktur respektieren. Keine Screenshots.

- [ ] **Step 1: Docs-Änderungen schreiben** (alle fünf Dateien; Kern-Botschaft pro Datei siehe oben — der Implementer formuliert im Stil der jeweiligen Datei aus)

- [ ] **Step 2: Manueller Smoke-Test (dokumentierte Verifikation, Ergebnis ins Task-Protokoll)**

```bash
# Frisches Datenverzeichnis erzwingen (Setup-Modus), Host starten:
cd src/Naudit.Web && Naudit__Db__ConnectionString="Data Source=/tmp/naudit-smoke/naudit.db" dotnet run
```

Erwartung im Log: `Setup-Modus: fehlende Pflicht-Konfiguration (…)`. Dann `curl -s http://localhost:5290/api/setup/status` → `"setupRequired":true`. Browser-Durchlauf (Wizard bis „Apply & restart", Provider Ollama ohne erreichbaren Server: „Test connection" darf fehlschlagen, Fortfahren muss gehen) — nach dem Apply-Neustart: `"setupRequired":false`, Webhook-Endpoint antwortet (401 bei falschem Secret), Settings-Seite zeigt die DB-Werte. Zum Schluss `/tmp/naudit-smoke` löschen.

- [ ] **Step 3: Voller Testlauf + Frontend-Gate**

Run: `dotnet test Naudit.slnx && cd src/frontend && npm run lint && npm run build`
Expected: alles grün

- [ ] **Step 4: Commit**

```bash
git add docs/ CLAUDE.md
git commit -m "docs(setup): Wizard-Quickstart, Setup-Modus, Spec-Praezisierungen"
```

---

## Self-Review-Notizen (bereits eingearbeitet)

- Spec-Coverage: Erkennung (`SetupStatus`) ✓, Setup-Modus-Mapping ✓, Schutz (Grafana-Muster + Login) ✓, Schritte 1–6 ✓, Draft (DP, eine Zeile) ✓, AI-Test (transienter Client, Fortfahren bei Fehlschlag) ✓, Apply atomar + Neustart + Abschluss mit Webhook-URLs ✓, DP-Secrets-nicht-entschlüsselbar ⇒ leerer Draft ✓, Tests It. Spec-Abschnitt ✓. **Nicht** in PR 2 (bewusst, Spec-PR-Schnitt): Manifest-Flow, GitLab-Hook-Anlage, „Verbindung testen" auf der Settings-Seite, Git-Token-Check.
- Bekannte akzeptierte Vereinfachungen: Admin-Anlage hat ein theoretisches Race (zwei parallele erste Admins mit verschiedenen Namen — irrelevant im Setup-Fenster); `MarkRestartPending`-No-Op-Thema aus PR 1 bleibt Fast-Follow.
- Konsistenzcheck Typen: `SetupDraft`-Record (C#) ↔ `SetupDraftDto`/`WizardDraft` (TS) Feld für Feld abgeglichen; Step-Props von Task 8/9 werden in Task 10 exakt mit diesen Signaturen aufgerufen.
