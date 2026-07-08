# Design: Setup-Wizard & selbstkonfigurierendes Naudit

*2026-07-08 · Projekt: Naudit*

## Ziel

Naudit soll **so einfach wie möglich startbar und einrichtbar** sein — für beide Szenarien:

1. **GitHub-App-Betrieb** (heutiger Einsatz): App anbinden mit so wenig manuellen
   Schritten wie möglich.
2. **Standalone/Unternehmens-Betrieb** (z. B. internes GitLab): Container starten,
   Browser öffnen, in wenigen Minuten produktiv — ohne Env-Var-Studium.

Kern: Ein **First-Run-Setup-Wizard in der WebUI** konfiguriert die App vollständig
(Admin-Konto, Git-Plattform, AI-Provider/Modell, Zugriffsmodell) und automatisiert
auch die Plattform-Seite (GitHub-App-Erstellung per Manifest-Flow, GitLab-Webhooks
per API). Das Setup-Ziel in den Docs:

```bash
docker run -p 8080:8080 -v naudit-data:/data ghcr.io/benediktnau/naudit
# → Browser öffnen → Wizard → fertig
```

## Entscheidungen

- **Die App konfiguriert sich selbst — Config lebt in der DB.** Das bisherige Prinzip
  „Config ist env-only" fällt; die Settings-Seite wird editierbar. Env-Vars bleiben als
  **Bootstrap und Override** erhalten (env gewinnt immer über DB).
- **Die DB wird Pflicht.** `Naudit:Db:Enabled` entfällt komplett; SQLite ist der
  Zero-Config-Default, Postgres bleibt Option. Es gibt keinen Headless-ohne-DB-Modus mehr.
- **`Naudit:Ui:Enabled` entfällt ebenfalls.** Die UI ist die Konfigurationsoberfläche und
  damit immer an (Login-geschützt wie bisher).
- **Volle Plattform-Automation:** GitHub-App-Erstellung per **App-Manifest-Flow**
  (ein Klick, Credentials kommen automatisch zurück), GitLab-Webhook-Anlage **per API**.
- **Ansatz „DB-Config-Provider + kontrollierter Neustart"** (statt Hot-Reload ohne
  Neustart): die DB wird eine normale Quelle in der .NET-Config-Pipeline, Änderungen
  werden per In-Process-Host-Neustart (~1–2 s) übernommen. Bewusst gegen den größeren
  Umbau auf per-Request-Auflösung entschieden — der Gewinn wäre minimal.
- **Umsetzung in drei fokussierten PRs** (Fundament → Wizard → Plattform-Automation),
  eine gemeinsame Spec (diese), drei Implementierungspläne.

## Architektur: Config-Modell & Bootstrap

### Settings-Tabelle

Neue Tabelle `Settings` im bestehenden `NauditDbContext`:

| Spalte | Bedeutung |
| --- | --- |
| `Key` (PK) | Config-Key in Doppelpunkt-Notation, z. B. `Naudit:Ai:Provider` |
| `Value` | Wert; bei Secrets der Data-Protection-Ciphertext |
| `IsSecret` | Steuert Verschlüsselung + Write-only-Verhalten der API |
| `UpdatedAtUtc` | Audit-Spur |

Secrets werden mit der vorhandenen **Data-Protection**-Infrastruktur verschlüsselt
(Purpose `Naudit.Settings`; die DP-Keys liegen bereits in der DB). Ehrliche Einordnung
für die Docs: Keyring und Secrets teilen dieselbe DB — die Verschlüsselung schützt
gegen versehentliches Anzeigen/Loggen und Backup-Leaks einzelner Tabellen, nicht gegen
einen Angreifer mit DB-Vollzugriff. Das ist der übliche Stand bei Self-Hosted-Tools
und akzeptiert. `AddDataProtection().PersistKeysToDbContext<NauditDbContext>()` wird
**unbedingt** registriert (heute nur bei aktivierter UI).

### NauditDbConfigurationProvider

Ein Custom `IConfigurationProvider` lädt die `Settings`-Tabelle beim Host-Bau und hängt
sich **vor** die Env-Vars in die Pipeline:

```
appsettings.json  <  DB-Settings  <  Env-Vars / User-Secrets
```

Env gewinnt immer (Bootstrap/Override). **`AddNauditInfrastructure` und sämtliche
Options bleiben unverändert** — sie lesen weiter `IConfiguration` und merken nicht,
woher die Werte kommen. Der Provider baut sich dafür einen minimalen
Bootstrap-Kontext (DbContextOptions aus den Bootstrap-Keys + Data Protection), führt
**vorher** `Database.Migrate()` aus und liest dann die Settings. Nicht entschlüsselbare
Secrets (Keyring weg) werden als **fehlend** behandelt, nicht als Fehler (→ Recovery).

### Bootstrap-Keys (bleiben prinzipbedingt env-only)

- `Naudit:Db:Provider` (`Sqlite` Default | `Postgres`)
- `Naudit:Db:ConnectionString` — neuer Default: `Data Source=data/naudit.db` (relativ,
  für den Binary-Fall); das Dockerfile setzt per `ENV` `Data Source=/data/naudit.db`
- `Naudit:ForwardedHeaders:*`, Ports/URLs (`ASPNETCORE_URLS`)

Alles andere (Plattform, Tokens, AI, UI-Auth-Provider, Gate, PostVerdict, Review-Tuning)
wird DB-fähig.

### Host-Schleife & IAppRestarter

`Program.cs` bekommt eine Schleife statt einmaligem `app.Run()`:

```csharp
while (true)
{
    var app = BuildNauditApp(args);   // Config-Pipeline inkl. DB-Provider neu aufbauen
    await app.RunAsync();
    if (!appRestarter.RestartRequested) break;
}
```

Ein `IAppRestarter`-Singleton (Flag + `IHostApplicationLifetime.StopApplication()`)
wird von Wizard/Settings nach „Übernehmen" aufgerufen. Funktioniert identisch im
Container und als Self-Contained-Binary; kein Container-Restart nötig. Der Host-Bau
läuft **in try/catch** (→ Recovery-Modus unten). Testbarkeit: die Schleife muss
`WebApplicationFactory`-kompatibel bleiben (WAF fängt den ersten Host-Bau ab; im Test
wird `IAppRestarter` durch einen Fake ersetzt, der nie wirklich neu startet).

### Zugriffsmodell: `Naudit:AccessGate:Mode`

Heute ist die Zugangsschranke implizit an die DB gekoppelt (DB an ⇒ `EfAccessGate`).
Mit Pflicht-DB wird das eine **explizite Einstellung** (Name bewusst `AccessGate`, um
Kollision mit dem Review-Severity-Gate `Naudit:Review:Gate` zu vermeiden):

- `Open` (**Default**): jedes Projekt mit gültigem Webhook-Secret wird reviewt
  (typisch Unternehmens-GitLab; entspricht dem Pre-WebUI-Verhalten).
- `Registered`: heutiges `EfAccessGate`-Verhalten — nur Projekte aktiver Accounts;
  empfohlen, wenn die GitHub App öffentlich installierbar ist.

## Setup-Modus & Wizard

### Erkennung

Beim Host-Bau prüft ein `SetupStatus`-Dienst die **effektive** Config (DB + env) auf das
Pflichtset, abhängig von der gewählten Plattform/dem Provider:

- GitLab: `BaseUrl` + `Token` + `WebhookSecret`
- GitHub (PAT): `Token` + `WebhookSecret`
- GitHub (App): `App:AppId` + `App:PrivateKey` + `WebhookSecret`
- AI: `Provider` + `Model`; bei Anthropic/OpenAICompatible zusätzlich `ApiKey`,
  bei Ollama/OpenAICompatible `Endpoint`

Fehlt etwas ⇒ **Setup-Modus**: gemappt sind nur `/health`, die Wizard-API
(`/api/setup/*`) und die SPA (zeigt den Wizard). Keine Webhooks, kein `/review`.
Env-komplette Deployments (z. B. das bestehende Coolify-Deployment) sehen den Wizard
**nie**.

### Schutz

Schritt 1 des Wizards ist das Anlegen des Admin-Kontos — nur möglich, solange **kein**
Admin existiert (Grafana/Portainer-Muster). Existiert bereits einer (z. B. per
`Naudit:Ui:Admin:*` geseedet — der env-Seed bleibt erhalten), verlangt der Wizard
dessen Login. Docs weisen auf das Zeitfenster hin: Instanz erst nach dem Setup
öffentlich exponieren.

### Schritte

1. **Admin-Konto** anlegen (oder Login, falls Admin existiert)
2. **Instanz-URL** — aus dem Request vorbefüllt, editierbar; gespeichert als neues
   `Naudit:PublicBaseUrl` (gebraucht für Webhook-URLs + Manifest-Redirect)
3. **Git-Plattform:**
   - **GitHub App** (empfohlen) — Manifest-Flow, siehe Plattform-Automation
   - **GitHub PAT** — Token-Feld + generiertes Webhook-Secret + Copy-Paste-Anleitung
   - **GitLab** — BaseUrl + Token (api-Scope); Webhook-Anlage per API, siehe unten;
     alternativ „überspringen" → manuelle Anleitung mit vorausgefüllten Werten
4. **AI-Provider:** Dropdown (Ollama / Anthropic / OpenAICompatible / ClaudeCode) mit
   bedingten Feldern (Modell, Endpoint, ApiKey) + **„Verbindung testen"** (Mini-Prompt
   über die vorhandene `AiClientFactory`, transienter Client aus den Draft-Werten)
5. **Zugriffsmodell:** `Open` | `Registered`, Empfehlung passend zur Plattform-Wahl
   (öffentliche GitHub App ⇒ `Registered`)
6. **Zusammenfassung → „Übernehmen & Neustart":** Draft wird atomar in `Settings`
   geschrieben, `IAppRestarter` löst den Neustart aus; Abschlussseite mit
   Webhook-URLs (für manuelle Pfade) und Statuscheck

### Draft

Der Wizard-Fortschritt liegt bis zum „Übernehmen" als **Draft** in der DB — eine
Zeile (`SetupDraft`: Id, JSON-Blob DP-verschlüsselt, UpdatedAtUtc). Nötig, weil der
Manifest-Flow die Seite verlässt und zurückkommt. Erst „Übernehmen" macht daraus echte
Settings; ein Abbruch verwirft nur den Draft.

## Plattform-Automation

### GitHub-App-Manifest-Flow

1. Naudit baut das Manifest: Name, `url` = PublicBaseUrl, `hook_attributes.url` =
   `{PublicBaseUrl}/webhook/github`, `redirect_url` =
   `{PublicBaseUrl}/api/setup/github/manifest-callback`, Permissions
   `pull_requests: write` / `contents: read`, Event `pull_request`, `public` je nach
   Wizard-Antwort („nur ich" vs. „öffentlich installierbar")
2. Browser-POST des Manifests an `{GitHub-Host}/settings/apps/new` (bzw.
   `/organizations/{org}/settings/apps/new`, Org optional wählbar; GitHub Enterprise
   über eigene BaseUrl) mit CSRF-`state`, das an den Draft gebunden ist
3. Nutzer bestätigt bei GitHub („Create GitHub App") → Redirect zurück mit `code`
4. Naudit tauscht den Code: `POST /app-manifests/{code}/conversions` → Antwort enthält
   **`id` (AppId), `pem` (PrivateKey), `webhook_secret`, `slug`** → in den Draft
5. Wizard zeigt den **„App installieren"-Link**
   (`{GitHub-Host}/apps/{slug}/installations/new`)

Der Code der Conversion ist kurzlebig (~1 h) und der Exchange braucht keine Auth —
funktioniert also auch, wenn Naudit (noch) nicht öffentlich erreichbar ist; nur der
spätere Webhook-Empfang braucht die öffentliche URL.

### GitLab-Webhook-Anlage

Mit dem eingegebenen Token legt Naudit die Hooks selbst an:

- pro Projekt: `POST /api/v4/projects/{id}/hooks` mit `url`, `token` (generiertes
  Secret), `merge_requests_events=true`
- pro Gruppe: `POST /api/v4/groups/{id}/hooks` (Hinweis im UI: Gruppen-Webhooks sind
  auf GitLab teilweise Premium-Tier — der Projekt-Pfad geht immer)

Eingabe: Projekt-IDs oder Gruppenpfad; Ergebnisliste **pro Projekt** (ok / 403 kein
Zugriff / 404 falsche ID). Teilerfolge sind okay und sichtbar.

## Settings-Seite (editierbar)

Die bisher read-only Settings-Seite wird für Admins editierbar, gruppiert nach
Plattform / AI / Review & Gate / Sign-in-Provider:

- **Secrets write-only:** die API gibt nie Werte zurück; das Feld zeigt nur „gesetzt",
  ein neuer Wert überschreibt.
- **Env-überschriebene Keys gesperrt** mit Badge „via environment" (env > DB — die UI
  macht die Vorrangregel sichtbar statt verwirrend).
- Speichern ⇒ **„Neustart erforderlich"-Banner** mit „Jetzt neu starten"-Button
  (`IAppRestarter`). Kein Auto-Restart bei jedem Save.
- **„Verbindung testen"** auch hier (AI-Ping; Git-Token-Check: GitLab `GET /user`,
  GitHub PAT `GET /user`, GitHub App JWT `GET /app`).

## Fehlerbehandlung & Recovery-Modus

- **Recovery-Modus (wichtigster Fall):** Wirft der Host-Bau mit DB-Config eine
  Config-Exception (z. B. App-Auth ohne PrivateKey), crasht die App **nicht** in einer
  Loop, sondern startet im Setup-/Recovery-Modus und zeigt den Fehler in der UI —
  korrigieren, neu starten. Heute (env-only) wäre das ein Crash-Loop im Orchestrator.
  Der Recovery-Modus ist wie der Wizard geschützt: existiert ein Admin, ist Login
  Pflicht — er ist nie ein offenes Config-Panel.
- **DP-Secrets nicht entschlüsselbar** (Keyring weg): betroffene Werte gelten als
  fehlend; der Wizard/Recovery fragt gezielt genau diese Secrets neu ab.
- **Manifest-Flow abgebrochen / Code ungültig:** Fehler im Schritt, Draft bleibt,
  erneut versuchbar.
- **GitLab-Webhook-Anlage:** Teilerfolge mit Status pro Projekt.
- **AI-Verbindungstest scheitert:** Warnung, Fortfahren erlaubt (Ollama ist z. B. oft
  erst später erreichbar).

## Migration & Kompatibilität

- **Env-komplette Bestandsdeployments laufen unverändert** — env gewinnt, der Wizard
  erscheint nicht.
- **Breaking (pre-release):** `Naudit:Db:Enabled` und `Naudit:Ui:Enabled` entfallen
  (Werte werden ignoriert; DB + UI sind immer an). Deployments, die bisher **ohne** DB
  liefen, bekommen beim nächsten Start automatisch eine SQLite-DB unter dem
  Default-Pfad — im Container ohne `/data`-Volume ist die flüchtig (Hinweis in Docs).
- **Breaking (pre-release):** Deployments mit bisher aktiver Zugangsschranke
  (DB war an) müssen einmal `Naudit__AccessGate__Mode=Registered` setzen — der neue
  Default ist `Open`.
- Neue EF-Migration (Settings + SetupDraft) wird wie gehabt **provider-neutral**
  nachgepflegt (SQLite-Typen raus, beide Annotationen rein — bekannte Prozedur aus
  CLAUDE.md).

## Tests

Bestehendes Muster, kein Netz:

- **Unit:** Config-Provider-Precedence (env > DB > appsettings); Secret-Verschlüsselung
  round-trip; Manifest-Bau + Conversion-Antwort-Mapping und GitLab-Hook-Anlage über
  `StubHttpMessageHandler`; `AccessGate:Mode`-Verhalten (Open/Registered);
  `SetupStatus`-Pflichtset-Logik je Plattform/Provider.
- **`WebApplicationFactory`:** unkonfiguriert ⇒ Wizard-API erreichbar, Webhooks 404;
  konfiguriert ⇒ umgekehrt; Settings-API liefert nie Secret-Werte; Admin-Anlage nur
  solange kein Admin existiert; `IAppRestarter` als Fake.
- **Host-Schleife/echter Neustart:** manuell verifiziert (wie der heutige E2E-Pfad).

## Umsetzung in drei PRs

1. **Fundament:** DB Pflicht (`Db:Enabled`/`Ui:Enabled` raus), `Settings`-Tabelle +
   Migration, `NauditDbConfigurationProvider`, Host-Schleife + `IAppRestarter` +
   Recovery-Modus, `AccessGate:Mode`, editierbare Settings-Seite. Docs-Update.
2. **Wizard:** Setup-Modus (`SetupStatus`), Wizard-SPA + `/api/setup/*` (Admin →
   Instanz-URL → Plattform manuell (PAT/GitLab ohne Auto-Hooks) → AI mit Test →
   Zugriffsmodell → Übernehmen), Draft-Persistenz.
3. **Plattform-Automation:** GitHub-App-Manifest-Flow (inkl. `state`-CSRF,
   Enterprise-BaseUrl, Org-Auswahl, Install-Link), GitLab-Webhook-Anlage per API.

**Docs-Payoff:** neues `docs/getting-started.md` („docker run → Browser → Wizard");
`configuration.md` trennt Bootstrap-Keys von verwalteten Keys; `deployment.md`
schrumpft (Env-Template nur noch Bootstrap + Overrides); `platform-setup.md`/
`github-app.md` verweisen auf den Wizard, manuelle Pfade bleiben dokumentiert;
CLAUDE.md-Abschnitt zum Config-Modell aktualisieren.

## Out of Scope

- Hot-Reload ohne Neustart (per-Request-Auflösung von `IChatClient`/`IGitPlatform`)
- Secret-Store-Backends (Vault/Key Vault) für die Settings
- Mehrere Git-Plattformen gleichzeitig in einer Instanz
- Config-Export/-Import über die UI
