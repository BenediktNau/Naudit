# Design: Geführter Claude-Login im Setup-Wizard

*2026-08-09 · Projekt: Naudit*

## Ziel

Provider `ClaudeCode` im Wizard wählen → **„Mit Claude anmelden"** klicken → im neuen Tab
einloggen → Code zurück einfügen → fertig. Kein lokal installiertes CLI, kein
`claude setup-token` auf dem eigenen Laptop, keine Umgebungsvariable, kein eigenes
Dockerfile.

Anlass: ein Kollege soll Naudit auf seinem Coolify-Server aufsetzen können, indem er das
fertige Image als Docker-Ressource einbindet und **alles** im UI konfiguriert. Sein
Claude-Abo soll dabei genauso im Wizard landen wie ein API-Key.

## Ausgangslage (verifiziert 2026-08-09)

- **Die `claude`-CLI liegt bereits im Haupt-Image** (`Dockerfile` Z. 86–103: Version über
  den `stable`-Zeiger aufgelöst, SHA256 gegen `manifest.json` geprüft, fail-closed). Im
  lokal gebauten Image bestätigt: `/usr/local/bin/claude`, Version 2.1.204,
  `HOME=/home/app` existiert und gehört `app` (uid 1654). **Ein abgeleitetes Dockerfile ist
  überflüssig** — `deploy/coolify/Dockerfile` ist nur noch `FROM …/naudit:latest`.
- **Env-Variablen sind nicht nötig.** Das Image setzt
  `Naudit__Db__ConnectionString=/data/naudit.db` selbst; der Admin wird vom Wizard angelegt
  (Grafana-Muster: nur solange keiner existiert); `PublicBaseUrl` wird aus dem Request
  vorbelegt. Nachgestellt: `docker run` ohne jede Variable ⇒ Host kommt im **Setup-Modus**
  hoch, `/health` 200, `/api/setup/status` meldet die fehlenden Pflichtschlüssel. Nötig ist
  einzig ein **Volume auf `/data`**.
- **Die Verkabelung für den Token steht komplett:** `SetupDraft.AiApiKey` →
  `DraftToSettings` → `Naudit:Ai:ApiKey` → `ClaudeCodeChatClient.cs:112` setzt daraus
  `CLAUDE_CODE_OAUTH_TOKEN` in die Kind-Umgebung.

### Die zwei Lücken

1. **`StepAi.tsx:19`** macht `needsKey` nur für `Anthropic`/`OpenAICompatible` wahr. Bei
   `ClaudeCode` gibt es **kein Feld**, in das ein Token passt.
2. **`SetupStatus.Check`** verlangt für `ClaudeCode` nichts (nur `Model` entfällt dort
   bewusst). Der Wizard wird also **grün ohne jede Auth**, und das erste Review scheitert.

## Wie `claude setup-token` sich fernsteuern lässt (gemessen, nicht vermutet)

Ohne Terminal gibt der Befehl **gar nichts** aus — ein Lauf mit umgeleitetem stdin/stdout
lief 12 s in den Timeout, stdout blieb leer. Es ist eine Ink-TUI und braucht ein
Pseudo-Terminal.

In einem PTY dagegen:

1. „Opening browser to sign in…",
2. dann die Authorize-URL
   `https://claude.com/cai/oauth/authorize?code=true&client_id=…&redirect_uri=https%3A%2F%2Fplatform.claude.com%2Foauth%2Fcode%2Fcallback&scope=user%3Ainference&code_challenge=…&code_challenge_method=S256&state=…`
   (als OSC-8-Hyperlink **und** als sichtbarer Text),
3. Wartezustand am Prompt `Paste code here if prompted >`,
4. nach dem eingefügten Code der Tausch gegen den langlebigen Token.

**Das PTY kostet keine Zeile unmanaged Code:** `script(1)` liegt bereits im Base-Image
(`/usr/bin/script` aus `bsdutils`, Debian trixie — Essential-Paket, im gebauten Image
verifiziert). `script -qec "claude setup-token" /dev/null` legt das Pseudo-Terminal an;
Naudit redet über ganz normale Pipes mit `script`. In einem Container nachgestellt: die
Authorize-URL kam sauber aus dem Transkript heraus.

Der Weg über die offizielle CLI wurde einer **eigenen PKCE-Implementierung** vorgezogen:
letztere müsste `client_id`, Authorize- und Token-Endpunkt fest verdrahten — undokumentierte
Fläche, die ohne Vorwarnung brechen kann und einen fremden OAuth-Client nachbaut. Die CLI
aktualisiert sich dagegen mit dem Image.

## Architektur

### 1 · `IClaudeLoginFlow` — eigene Naht neben `IProcessRunner`

Der bestehende `IProcessRunner` passt **nicht**: er ist run-to-completion (`RunAsync` →
`ProcessResult`). Hier lebt ein Prozess **zwischen zwei HTTP-Requests**. Also eine schmale
eigene Naht in `src/Naudit.Infrastructure/Ai/ClaudeCode/`; `SystemProcessRunner` bleibt
unangetastet.

```
Task<string>  StartAsync(ct)             // spawnt, scrapt die Authorize-URL
Task<string>  SubmitCodeAsync(code, ct)  // schreibt den Code, liefert den Token
void          Cancel()                   // killt den Prozess, räumt auf
```

Default-Impl `ScriptClaudeLoginFlow`, prozessweiter Singleton, **höchstens eine** aktive
Sitzung, Selbstverfall nach 10 Minuten:

- **Spawn:** `script -qec "claude setup-token" /dev/null`, stdin/stdout als Pipes,
  `TERM=xterm-256color`, `HOME` **und** `CLAUDE_CONFIG_DIR` auf ein frisches 0700-Temp-Dir.
- **URL scrapen:** inkrementell lesen bis `https://claude\.com/cai/oauth/authorize\?…`
  matcht (max. 45 s — die CLI spinnt erst ein paar Sekunden). ANSI-/OSC-8-Rauschen vorher
  filtern.
- **Code einreichen:** `code + "\r"` auf stdin, lesen bis Token-Regex
  (`sk-ant-[A-Za-z0-9_-]{20,}`) oder Prozessende (max. 60 s). **Fallback:**
  `<configDir>/.credentials.json` → `claudeAiOauth.accessToken` (Struktur verifiziert).
- **Aufräumen:** Prozess killen und Temp-Dir löschen im `finally` — dort liegen
  Credentials. Gleiches Muster wie die MCP-Temp-Datei in `ClaudeCodeChatClient.cs:52-75`.
- **Verfügbarkeitsprobe:** fehlt `script` oder `claude`, meldet `StartAsync` das sauber,
  statt zu hängen.

### 2 · API — drei Endpunkte in `SetupEndpoints.cs`

In der bestehenden `group` (`RequireAuthorization` + `CurrentAccount.GetAdminAsync`), also
nur im Setup-Modus und nur für Admins:

| Endpunkt | Verhalten |
|---|---|
| `POST /api/setup/claude/login/start` | → `{ authorizeUrl }` \| 400 `{ error }` |
| `POST /api/setup/claude/login/code` | `{ code }` → Token in `SetupDraft.AiApiKey`, Antwort `{ ok: true }` |
| `POST /api/setup/claude/login/cancel` | → 204 |

Der Token geht **nie** an den Browser zurück: `DraftResponseAsync` nullt `AiApiKey` bereits
und liefert nur `hasAiApiKey`. Ab da läuft alles über den vorhandenen Pfad.

### 3 · Pflichtprüfung — `SetupStatus.Check`

Für `ClaudeCode` wird ein Token Pflicht, erfüllbar durch `Naudit:Ai:ApiKey` **oder** die
Umgebungsvariable `CLAUDE_CODE_OAUTH_TOKEN` (über den Standard-Env-Provider als
`config["CLAUDE_CODE_OAUTH_TOKEN"]` sichtbar). Der heutige Env-Weg bleibt damit gültig und
sperrt das Feld in der UI wie jeder andere env-gesetzte Schlüssel.

### 4 · `StepAi.tsx`

Bei `ClaudeCode` ein „Claude subscription"-Block:

- **Primär:** Knopf „Sign in with Claude" → URL als Link (neuer Tab) + Kopier-Knopf,
  darunter Code-Feld und „Submit", danach grüner Haken.
- **Sekundär, eingeklappt:** „Already have a token?" → das vorhandene Passwortfeld auf
  `aiApiKey`. Das ist der Rückfallweg **und** der Weg für alle, die schon einen Token haben.
- `keyOk` (Z. 21) muss `ClaudeCode` einschließen, sonst bleibt „Continue" fälschlich frei.
- „Test connection" bleibt und ist hier besonders wertvoll: `AiTestClientFactory` →
  `AiClientFactory.Create` → für `ClaudeCode` ein **echter** `claude`-Aufruf im Container.
  Nach dem Login also ein Ende-zu-Ende-Beweis, dass Abo und CLI zusammenspielen. Die
  Registrierung liegt in `Program.cs:90`, **vor** `AddNauditInfrastructure` — der Test
  funktioniert im Setup-Modus.

## Datenfluss

```
StepAi "Sign in with Claude"
  └─ POST /api/setup/claude/login/start
       └─ ScriptClaudeLoginFlow.StartAsync
            └─ script -qec "claude setup-token" /dev/null      (PTY, eigenes HOME)
                 └─ stdout scrapen ──▶ authorizeUrl ──▶ Browser (neuer Tab)
Nutzer loggt auf claude.com ein, kopiert den Code
  └─ POST /api/setup/claude/login/code { code }
       └─ SubmitCodeAsync: "code\r" → stdin,  Token aus stdout
                                      (Fallback: .credentials.json)
            └─ SetupDraft.AiApiKey (DP-verschlüsselt)
                 └─ apply ──▶ Naudit:Ai:ApiKey ──▶ CLAUDE_CODE_OAUTH_TOKEN
```

## Fehlerbehandlung

- **`script`/`claude` fehlt, URL erscheint nicht, Prozess stirbt:** `start` antwortet 400
  mit Klartext; die UI zeigt „geführter Login nicht verfügbar — Token einfügen". **Nie eine
  Sackgasse**, das Paste-Feld ist immer sichtbar.
- **Falscher/abgelaufener Code:** die CLI meldet es; `SubmitCodeAsync` läuft in seinen
  Timeout oder findet keinen Token ⇒ 400, Sitzung bleibt für einen zweiten Versuch offen.
- **Sitzung vergessen:** Selbstverfall nach 10 min, Prozess und Temp-Dir werden abgeräumt.
- **Zweiter paralleler Login:** der laufende wird abgebrochen und ersetzt (höchstens eine
  Sitzung).

## Testansatz

Repo-Stil: kein Netz, kein echter Prozess.

- **`SetupStatusTests`:** ClaudeCode ohne Token ⇒ `missing`; mit `Naudit:Ai:ApiKey` ⇒ ok;
  mit `CLAUDE_CODE_OAUTH_TOKEN` in der Umgebung ⇒ ok.
- **Endpunkt-Tests** (`WebApplicationFactory` + `FakeClaudeLoginFlow`): `start` liefert die
  URL; `code` landet im Draft (`hasAiApiKey: true`, **kein** Token im Body); Nicht-Admin ⇒
  403; außerhalb des Setup-Modus nicht gemappt.
- **Parser-Test gegen ein echtes PTY-Transkript** als Fixture — am 2026-08-09 wurden 3.609
  Bytes realer CLI-Ausgabe mit ANSI-/OSC-8-Rauschen aufgezeichnet. Der wertvollste Test:
  er nagelt das Scraping an echte Ausgabe statt an eine Wunschvorstellung.
- **Opt-in-Integrationstest** `NAUDIT_TEST_CLAUDE_LOGIN` (Muster wie `NAUDIT_TEST_POSTGRES`),
  der wirklich `script`+`claude` startet und nur die URL prüft — in CI übersprungen.

## Risiken, ehrlich benannt

- **Screen-Scraping einer TUI.** Ein CLI-Redesign kann die URL-Erkennung brechen. Deshalb
  ist der Paste-Weg immer sichtbar und ein Fehlschlag meldet sich als solcher. Dass die
  Maschine hier CLI 2.1.204 hat und die Entwicklungsmaschine 2.1.226, ist genau der Grund
  für den Fixture-Test **plus** den `.credentials.json`-Fallback.
- **`script` ist ein Base-Image-Detail.** Heute Essential, morgen vielleicht nicht.
  `bsdutils` wird explizit in die vorhandene `apt-get`-Zeile der Runtime-Stage
  aufgenommen — billige Versicherung.
- **`\r` vs. `\n`** in den Ink-Prompt ist nicht abschließend geklärt; das entscheidet der
  manuelle Container-Test.
- **Der Token liegt anschließend in der DB**, DP-verschlüsselt — aber die
  Data-Protection-Schlüssel liegen unverschlüsselt in **derselben** Datei (der Host loggt
  beim Start „No XML encryptor configured"). Das `/data`-Volume ist damit der eigentliche
  Vertrauensbereich. Bestand, nicht von diesem Vorhaben verursacht, aber mit einem
  Jahres-Abo-Token darin schärfer — hier bewusst nur benannt, nicht behandelt.

## Bewusst nicht drin

- **Derselbe Login auf der Profilseite** (Autor-Sessions, `/api/me/claude-session`) und in
  den Settings nach dem Setup. Die Naht ist wiederverwendbar; der Umfang bleibt vorerst der
  Wizard.
- **Compose-Artefakt für Coolify** und ein **Selbstcheck-Panel** nach dem Setup — beides
  erwogen und abgewählt.
- **Doku-Neuschnitt** von `deployment.md`/`getting-started.md`. Eine knappe Sektion in
  `docs/claudecode-provider.md` gehört dagegen zum Feature und wird mitgeschrieben.

## Abnahme

`docker build` lokal → Container mit Volume auf `/data`, Port 8080, **null Env** → Wizard
durchklicken → Claude-Login → GitHub-App per Manifest → Webhook feuern. Für den
Manifest-Flow gilt: der Redirect funktioniert auch gegen `localhost` (GitHub leitet den
Browser um), die **Webhook-Zustellung** braucht dagegen die öffentliche Domain.
