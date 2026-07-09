# Setup-Wizard Plattform-Automation (PR 3/3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Plattform-Automation im Setup-Wizard: GitHub-App-Erstellung per **Manifest-Flow** (ein Klick — der Browser POSTet ein Manifest zu GitHub, GitHub redirected mit `?code=` zurück, Naudit tauscht den Code gegen AppId/PrivateKey/WebhookSecret/Slug und legt sie in den Draft; der Wizard zeigt den Install-Link) und **GitLab-Webhook-Anlage per API** (Projekt-IDs/Gruppen eingeben, Ergebnis pro Ziel, Teilerfolge sichtbar). Die manuellen Pfade aus PR 2 (GitHub-PAT, GitLab-Copy-Paste-Webhook) bleiben erhalten.

**Architecture:** Drei neue Infrastructure-Bausteine unter `Setup/`: `GitHubManifest` (pures Manifest-/URL-Bauen inkl. GHES-API-Base-Ableitung, kein HTTP), `GitHubManifestConverter` und `GitLabHookCreator` (HTTP über injizierten `HttpClient`, unit-getestet mit `StubHttpMessageHandler` — das bestehende Muster der Git-Clients). Im Web drei neue Setup-Endpoints (`POST /api/setup/github/manifest`, `GET /api/setup/github/manifest-callback`, `POST /api/setup/gitlab/hooks`); HTTP im Setup-Modus läuft über die neue Seam `SetupHttpClientFactory` (Muster `AiTestClientFactory`), weil `AddNauditInfrastructure` — und damit jeder `IHttpClientFactory` — im Setup-Modus nicht registriert ist. `SetupDraft` wächst um die App-Felder, `DraftToSettings` bekommt den `Auth=App`-Zweig. SPA: `StepPlatform` mit drei Karten (GitHub App empfohlen / GitHub PAT / GitLab), Manifest-Form-POST (volle Navigation weg von der Seite), Rückkehr-Resume über `?setup=…`-Query-Param, GitLab-Hook-UI mit Ergebniszeile pro Ziel.

**Tech Stack:** wie PR 2 — .NET 10 Minimal API, EF Core, ASP.NET Data Protection, xUnit + `WebApplicationFactory` + `StubHttpMessageHandler`, React + TS + Tailwind 4.

**Basis:** `main` (PR #43/#44 gemerged); neuer Branch z. B. `feat/setup-wizard-3`. Spec: `docs/superpowers/specs/2026-07-08-setup-wizard-design.md`, Abschnitte „Plattform-Automation", „Fehlerbehandlung & Recovery-Modus", PR-Punkt 3.

## Global Constraints

- Solution-Datei ist `Naudit.slnx` — **nie** `Naudit.sln` (MSB1009).
- Code-Kommentare **Deutsch**; UI-Texte und `docs/` **Englisch**; Commit-Messages Deutsch mit ae/oe/ue statt Umlauten.
- Core-Regel: **dieser PR fasst `Naudit.Core` nicht an.** Alles Neue liegt in Infrastructure (`Setup/`), Web (`Endpoints/`, Seam-Records) und `src/frontend/`.
- **Keine neuen NuGet-/npm-Pakete. Keine neue EF-Migration** — der Draft ist ein JSON-Blob, Settings sind Key-Value. **`SettingsCatalog` braucht keine neuen Keys**: `Naudit:GitHub:Auth`, `App:AppId`, `App:PrivateKey`, `GitHub:BaseUrl`, `GitHub:WebhookSecret` sind bereits whitelisted. Der App-**Slug** wird bewusst NICHT als Setting persistiert — er lebt nur im Draft (Install-Link); zur Laufzeit holt `GitHubAppInstallationChecker` den Slug live über `GET /app`.
- **Setup-Modus hat keinen `IHttpClientFactory`** (der lebt in `AddNauditInfrastructure`, das dort nicht läuft). HTTP in Setup-Endpoints ausschließlich über die Seam `SetupHttpClientFactory` (Task 5) — Produktion `() => new HttpClient()` (kurzlebige Einmal-Aufrufe im Wizard, kein Socket-Exhaustion-Thema), Tests injizieren `new HttpClient(stub)`.
- **Secrets:** `GitHubAppPrivateKey` (PEM) und `GitHubManifestState` erscheinen NIE in einer API-Antwort (GET maskiert sie auf `null`); `PUT /api/setup/draft` kann sie weder setzen noch löschen (serverseitig verwaltet, nur der Manifest-Callback schreibt sie). Das `WebhookSecret` bleibt wie in PR 2 bewusst sichtbar.
- **Callback bewusst anonym:** `GET /api/setup/github/manifest-callback` verlangt KEINEN Cookie. Credential ist der unratbare, an den Draft gebundene, einmal verwendbare `state` (32 Zufallsbytes hex, constant-time verglichen). Grund: der externe Redirect darf nicht an Cookie-Verlust scheitern; ein Angreifer kann den state nicht kennen (er geht nur an GitHub und den Admin-Browser). Die Antwort ist immer ein Redirect auf die SPA (`/?setup=github-app-created` bzw. `/?setup=github-app-error&reason=state|conversion`), nie JSON. Der Endpoint existiert nur im Setup-Modus (sonst 404 via `/api`-Fallback).
- **GHES-Ableitung:** Web-Host `https://github.com` ⇒ API `https://api.github.com`; jeder andere Host ⇒ `{host}/api/v3`. Bei GHES schreibt Apply zusätzlich `Naudit:GitHub:BaseUrl`. Bekannte, akzeptierte Limitation (NICHT Teil dieses PRs): `GitHubAppInstallationChecker` baut den Install-Link des Onboarding-Banners weiterhin hart auf `https://github.com`.
- **GitLab-Hook-Anlage idempotent:** existiert am Ziel schon ein Hook mit derselben URL, wird keiner angelegt („already exists" zählt als ok). Teilerfolge sind 200 mit Ergebnis pro Ziel; der Endpoint wirft nie 500 wegen eines einzelnen Ziels.
- **`SetupDraft`-Erweiterung nur am ENDE der Parameterliste** (JSON deserialisiert by-name; positionale Konstruktor-Aufrufe in Tests bleiben gültig).
- WAF-Gotchas aus PR 2 gelten weiter: `UseSetting(key, "")` = fehlend für `SetupStatus`, aber env-locked für Apply. **Für Apply-Tests des App-Zweigs deshalb `new TestAppFactory().WithoutGitHubBaseline()`** — sonst überspringt Apply `Naudit:GitHub:WebhookSecret`, weil der Key per Baseline env-gesetzt ist.
- Bestehende Tests bleiben grün: `dotnet test Naudit.slnx`. Frontend-Gate: `cd src/frontend && npm run lint && npm run build`.
- Task-Abhängigkeiten: 1–3 unabhängig; 4 vor 5; 5 vor 7; 6 vor 8; 9 zuletzt.

---

### Task 1: GitHubManifest — Manifest-JSON + Host-Ableitung (pur)

**Files:**
- Create: `src/Naudit.Infrastructure/Setup/GitHubManifest.cs`
- Test: `tests/Naudit.Tests/GitHubManifestTests.cs`

**Interfaces:**
- Consumes: nichts (reine Funktionen).
- Produces: `GitHubManifest.Build/ApiBase/CreateAppUrl/InstallUrl/Normalize`, Records `GitHubAppManifest`/`GitHubAppManifestHook` — verwendet von Task 2 (Converter), Task 4 (Apply-GHES-Zweig) und Task 5 (Endpoints).

- [ ] **Step 1: Failing Tests schreiben**

```csharp
// tests/Naudit.Tests/GitHubManifestTests.cs
using System.Text.Json;
using Naudit.Infrastructure.Setup;
using Xunit;

namespace Naudit.Tests;

/// <summary>Manifest-Bau + Host-Ableitung fuer den GitHub-App-Manifest-Flow.
/// Alles pur — HTTP macht erst der Converter (Task 2).</summary>
public sealed class GitHubManifestTests
{
    [Fact]
    public void Build_setztHookRedirectPermissionsEvents()
    {
        var m = GitHubManifest.Build("https://naudit.example.com/", "naudit", isPublic: false);
        Assert.Equal("naudit", m.Name);
        Assert.Equal("https://naudit.example.com", m.Url);
        Assert.Equal("https://naudit.example.com/webhook/github", m.HookAttributes.Url);
        Assert.True(m.HookAttributes.Active);
        Assert.Equal("https://naudit.example.com/api/setup/github/manifest-callback", m.RedirectUrl);
        Assert.False(m.Public);
        Assert.Equal("write", m.DefaultPermissions["pull_requests"]);
        Assert.Equal("read", m.DefaultPermissions["contents"]);
        Assert.Equal(["pull_request"], m.DefaultEvents);
    }

    [Fact]
    public void Build_serialisiertSnakeCase()
    {
        // GitHub erwartet exakt diese Feldnamen im Form-Feld "manifest".
        var json = JsonSerializer.Serialize(GitHubManifest.Build("https://n.example", "naudit", isPublic: true));
        Assert.Contains("\"hook_attributes\"", json);
        Assert.Contains("\"redirect_url\"", json);
        Assert.Contains("\"default_permissions\"", json);
        Assert.Contains("\"default_events\"", json);
        Assert.Contains("\"public\":true", json);
    }

    [Fact]
    public void ApiBase_githubCom_vs_Ghes()
    {
        Assert.Equal("https://api.github.com", GitHubManifest.ApiBase(null));
        Assert.Equal("https://api.github.com", GitHubManifest.ApiBase("https://github.com/"));
        Assert.Equal("https://ghes.example.com/api/v3", GitHubManifest.ApiBase("https://ghes.example.com"));
    }

    [Fact]
    public void CreateAppUrl_mitUndOhneOrg()
    {
        Assert.Equal("https://github.com/settings/apps/new?state=abc",
            GitHubManifest.CreateAppUrl(null, null, "abc"));
        Assert.Equal("https://ghes.example.com/organizations/my-org/settings/apps/new?state=abc",
            GitHubManifest.CreateAppUrl("https://ghes.example.com/", " my-org ", "abc"));
    }

    [Fact]
    public void InstallUrl_ausSlug()
    {
        Assert.Equal("https://github.com/apps/naudit-test/installations/new",
            GitHubManifest.InstallUrl(null, "naudit-test"));
    }
}
```

- [ ] **Step 2: Tests laufen lassen — müssen fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubManifestTests`
Expected: FAIL (Compile-Fehler: `GitHubManifest` existiert nicht)

- [ ] **Step 3: Implementierung**

```csharp
// src/Naudit.Infrastructure/Setup/GitHubManifest.cs
using System.Text.Json.Serialization;

namespace Naudit.Infrastructure.Setup;

/// <summary>Das GitHub-App-Manifest, wie GitHub es im Form-Feld "manifest" erwartet
/// (snake_case per JsonPropertyName — auch wenn der Host camelCase serialisiert).</summary>
public sealed record GitHubAppManifest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("hook_attributes")] GitHubAppManifestHook HookAttributes,
    [property: JsonPropertyName("redirect_url")] string RedirectUrl,
    [property: JsonPropertyName("public")] bool Public,
    [property: JsonPropertyName("default_permissions")] IReadOnlyDictionary<string, string> DefaultPermissions,
    [property: JsonPropertyName("default_events")] IReadOnlyList<string> DefaultEvents,
    [property: JsonPropertyName("description")] string Description);

public sealed record GitHubAppManifestHook(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("active")] bool Active = true);

/// <summary>Baut Manifest und URLs fuer den GitHub-App-Manifest-Flow. Reine Funktionen —
/// der Browser POSTet das Manifest an {WebHost}/settings/apps/new, GitHub redirected mit
/// ?code= zurueck (Exchange macht GitHubManifestConverter). GHES: API liegt unter /api/v3.</summary>
public static class GitHubManifest
{
    public const string DefaultWebHost = "https://github.com";

    /// <summary>Leer ⇒ github.com; sonst Host getrimmt, ohne Slash am Ende.</summary>
    public static string Normalize(string? webHost) =>
        string.IsNullOrWhiteSpace(webHost) ? DefaultWebHost : webHost.Trim().TrimEnd('/');

    /// <summary>github.com ⇒ api.github.com; GHES ⇒ {host}/api/v3.</summary>
    public static string ApiBase(string? webHost)
    {
        var host = Normalize(webHost);
        return host == DefaultWebHost ? "https://api.github.com" : $"{host}/api/v3";
    }

    public static string CreateAppUrl(string? webHost, string? org, string state)
    {
        var path = string.IsNullOrWhiteSpace(org)
            ? "/settings/apps/new"
            : $"/organizations/{Uri.EscapeDataString(org.Trim())}/settings/apps/new";
        return $"{Normalize(webHost)}{path}?state={Uri.EscapeDataString(state)}";
    }

    public static string InstallUrl(string? webHost, string slug) =>
        $"{Normalize(webHost)}/apps/{Uri.EscapeDataString(slug)}/installations/new";

    /// <summary>Permissions/Events entsprechen dem, was Naudit braucht: PRs kommentieren
    /// (pull_requests:write), Code lesen (contents:read), Event pull_request (Spec).</summary>
    public static GitHubAppManifest Build(string publicBaseUrl, string appName, bool isPublic)
    {
        var baseUrl = publicBaseUrl.TrimEnd('/');
        return new GitHubAppManifest(
            Name: appName,
            Url: baseUrl,
            HookAttributes: new GitHubAppManifestHook($"{baseUrl}/webhook/github"),
            RedirectUrl: $"{baseUrl}/api/setup/github/manifest-callback",
            Public: isPublic,
            DefaultPermissions: new Dictionary<string, string> { ["pull_requests"] = "write", ["contents"] = "read" },
            DefaultEvents: ["pull_request"],
            Description: "Naudit code review bot");
    }
}
```

- [ ] **Step 4: Tests laufen lassen — müssen bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubManifestTests`
Expected: PASS (5 Tests)

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Setup/GitHubManifest.cs tests/Naudit.Tests/GitHubManifestTests.cs
git commit -m "feat(setup): GitHubManifest - Manifest-JSON und Host-Ableitung fuer den App-Flow"
```

---

### Task 2: GitHubManifestConverter — Code-Exchange

**Files:**
- Create: `src/Naudit.Infrastructure/Setup/GitHubManifestConverter.cs`
- Test: `tests/Naudit.Tests/GitHubManifestConverterTests.cs`

**Interfaces:**
- Consumes: `HttpClient` (ctor), `GitHubManifest.ApiBase` (Task 1), `Fakes/StubHttpMessageHandler`.
- Produces: `GitHubManifestConverter.ConvertAsync(webHost, code, ct)` → `GitHubManifestConversion(AppId, PrivateKey, WebhookSecret, Slug)`; Fehler ⇒ `InvalidOperationException`. Verwendet vom Callback-Endpoint (Task 5).

- [ ] **Step 1: Failing Tests schreiben**

```csharp
// tests/Naudit.Tests/GitHubManifestConverterTests.cs
using System.Net;
using Naudit.Infrastructure.Setup;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>Exchange POST /app-manifests/{code}/conversions — braucht keine Auth,
/// der kurzlebige Code ist das Credential. 201 ⇒ App-Credentials, sonst Exception.</summary>
public sealed class GitHubManifestConverterTests
{
    private const string ConversionJson = """
        { "id": 4711, "slug": "naudit-test",
          "pem": "-----BEGIN RSA PRIVATE KEY-----\nX\n-----END RSA PRIVATE KEY-----",
          "webhook_secret": "hook-geheim", "client_id": "Iv1.x",
          "html_url": "https://github.com/apps/naudit-test" }
        """;

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body) };

    [Fact]
    public async Task Convert_ruftConversionsEndpoint_undMapptFelder()
    {
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.Created, ConversionJson));
        var converter = new GitHubManifestConverter(new HttpClient(stub));

        var result = await converter.ConvertAsync("https://github.com", "code-123");

        Assert.Equal("https://api.github.com/app-manifests/code-123/conversions",
            stub.LastRequest!.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, stub.LastRequest.Method);
        Assert.Equal("4711", result.AppId);
        Assert.StartsWith("-----BEGIN RSA", result.PrivateKey);
        Assert.Equal("hook-geheim", result.WebhookSecret);
        Assert.Equal("naudit-test", result.Slug);
    }

    [Fact]
    public async Task Convert_Ghes_nutztApiV3()
    {
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.Created, ConversionJson));
        await new GitHubManifestConverter(new HttpClient(stub)).ConvertAsync("https://ghes.example.com/", "c");
        Assert.Equal("https://ghes.example.com/api/v3/app-manifests/c/conversions",
            stub.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Convert_nicht201_wirft()
    {
        // Abgelaufener/ungueltiger Code: GitHub antwortet 404 — Fehler im Schritt, Draft bleibt (Spec).
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubManifestConverter(new HttpClient(stub)).ConvertAsync("https://github.com", "alt"));
    }

    [Fact]
    public async Task Convert_ohneWebhookSecret_wirft()
    {
        // Fail-closed: ohne webhook_secret kann Naudit die HMAC-Signatur nicht pruefen.
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.Created,
            """{ "id": 1, "slug": "s", "pem": "PEM", "webhook_secret": null }"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubManifestConverter(new HttpClient(stub)).ConvertAsync("https://github.com", "c"));
    }
}
```

- [ ] **Step 2: Tests laufen lassen — müssen fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubManifestConverterTests`
Expected: FAIL (Compile-Fehler)

- [ ] **Step 3: Implementierung**

```csharp
// src/Naudit.Infrastructure/Setup/GitHubManifestConverter.cs
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Naudit.Infrastructure.Setup;

/// <summary>Ergebnis der Manifest-Conversion: alles, was der Draft fuer Auth=App braucht.</summary>
public sealed record GitHubManifestConversion(string AppId, string PrivateKey, string WebhookSecret, string Slug);

/// <summary>Tauscht den Manifest-Code: POST {api}/app-manifests/{code}/conversions.
/// Braucht KEINE Auth (der ~1h gueltige Code ist das Credential) — funktioniert also auch,
/// bevor Naudit oeffentlich erreichbar ist. Jeder Fehler ⇒ InvalidOperationException,
/// der Callback-Endpoint uebersetzt das in einen Redirect mit Fehler-Flag.</summary>
public sealed class GitHubManifestConverter(HttpClient http)
{
    public async Task<GitHubManifestConversion> ConvertAsync(string? webHost, string code, CancellationToken ct = default)
    {
        var url = $"{GitHubManifest.ApiBase(webHost)}/app-manifests/{Uri.EscapeDataString(code)}/conversions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("User-Agent", "Naudit");
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException($"GitHub manifest conversion failed ({(int)res.StatusCode}).");

        var dto = JsonSerializer.Deserialize<ConversionDto>(body)
            ?? throw new InvalidOperationException("GitHub manifest conversion returned an empty body.");
        if (dto.Id <= 0 || string.IsNullOrWhiteSpace(dto.Pem)
            || string.IsNullOrWhiteSpace(dto.WebhookSecret) || string.IsNullOrWhiteSpace(dto.Slug))
            throw new InvalidOperationException("GitHub manifest conversion returned incomplete app credentials.");

        return new GitHubManifestConversion(
            dto.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            dto.Pem, dto.WebhookSecret, dto.Slug);
    }

    private sealed record ConversionDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("slug")] string? Slug,
        [property: JsonPropertyName("pem")] string? Pem,
        [property: JsonPropertyName("webhook_secret")] string? WebhookSecret);
}
```

- [ ] **Step 4: Tests laufen lassen — müssen bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubManifestConverterTests`
Expected: PASS (4 Tests)

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Setup/GitHubManifestConverter.cs tests/Naudit.Tests/GitHubManifestConverterTests.cs
git commit -m "feat(setup): GitHubManifestConverter - Code-Exchange app-manifests/conversions"
```

---

### Task 3: GitLabHookCreator — Webhook-Anlage mit Ergebnis pro Ziel

**Files:**
- Create: `src/Naudit.Infrastructure/Setup/GitLabHookCreator.cs`
- Test: `tests/Naudit.Tests/GitLabHookCreatorTests.cs`

**Interfaces:**
- Consumes: `HttpClient` (ctor), `Fakes/StubHttpMessageHandler`.
- Produces: `GitLabHookCreator.CreateAsync(baseUrl, token, webhookUrl, secret, targets, ct)` → `IReadOnlyList<GitLabHookResult>`; `GitLabHookTarget(Kind, IdOrPath)`, `GitLabHookTargetKind { Project, Group }`, `GitLabHookResult(Target, Ok, Status, Detail)`. Verwendet vom Hooks-Endpoint (Task 6).

- [ ] **Step 1: Failing Tests schreiben**

```csharp
// tests/Naudit.Tests/GitLabHookCreatorTests.cs
using System.Net;
using System.Text;
using Naudit.Infrastructure.Setup;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>Webhook-Anlage per GitLab-API: Ergebnis pro Ziel, Teilerfolge okay,
/// idempotent (URL schon vorhanden ⇒ skip), wirft nie.</summary>
public sealed class GitLabHookCreatorTests
{
    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Projekt_legtHookAn_mitTokenUndMrEvents()
    {
        var stub = new StubHttpMessageHandler(req =>
            req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, "[]") : Json(HttpStatusCode.Created, "{}"));
        var creator = new GitLabHookCreator(new HttpClient(stub));

        var results = await creator.CreateAsync("https://gitlab.example.com/", "glpat-x",
            "https://naudit.example.com/webhook/gitlab", "hook-secret",
            [new GitLabHookTarget(GitLabHookTargetKind.Project, "42")]);

        Assert.True(results.Single().Ok);
        var post = stub.Calls.Single(c => c.Method == HttpMethod.Post);
        Assert.Equal("https://gitlab.example.com/api/v4/projects/42/hooks", post.Uri!.ToString());
        Assert.Contains("\"merge_requests_events\":true", post.Body);
        Assert.Contains("\"push_events\":false", post.Body);
        Assert.Contains("hook-secret", post.Body);
        Assert.Contains("https://naudit.example.com/webhook/gitlab", post.Body);
        Assert.Equal("glpat-x", stub.LastRequest!.Headers.GetValues("PRIVATE-TOKEN").Single());
    }

    [Fact]
    public async Task Gruppe_nutztGroupsPfad_undUrlCodiert()
    {
        var stub = new StubHttpMessageHandler(req =>
            req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, "[]") : Json(HttpStatusCode.Created, "{}"));
        await new GitLabHookCreator(new HttpClient(stub)).CreateAsync("https://gitlab.example.com", "t",
            "https://n.example/webhook/gitlab", "s",
            [new GitLabHookTarget(GitLabHookTargetKind.Group, "my-group/sub")]);
        var post = stub.Calls.Single(c => c.Method == HttpMethod.Post);
        Assert.Equal("https://gitlab.example.com/api/v4/groups/my-group%2Fsub/hooks", post.Uri!.ToString());
    }

    [Fact]
    public async Task VorhandenerHook_wirdUebersprungen()
    {
        var stub = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """[{"id":1,"url":"https://naudit.example.com/webhook/gitlab"}]"""));
        var results = await new GitLabHookCreator(new HttpClient(stub)).CreateAsync(
            "https://gitlab.example.com", "t", "https://naudit.example.com/webhook/gitlab", "s",
            [new GitLabHookTarget(GitLabHookTargetKind.Project, "42")]);
        Assert.True(results.Single().Ok);
        Assert.Contains("already exists", results.Single().Detail);
        Assert.DoesNotContain(stub.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Fehler403Und404_werdenProZielGemappt()
    {
        var stub = new StubHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/groups/")
                ? Json(HttpStatusCode.Forbidden, "{}")
                : req.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, "[]") : Json(HttpStatusCode.NotFound, "{}"));
        var results = await new GitLabHookCreator(new HttpClient(stub)).CreateAsync(
            "https://gitlab.example.com", "t", "https://n.example/webhook/gitlab", "s",
            [new GitLabHookTarget(GitLabHookTargetKind.Project, "99"),
             new GitLabHookTarget(GitLabHookTargetKind.Group, "grp")]);

        var project = results.Single(r => r.Target.Kind == GitLabHookTargetKind.Project);
        Assert.False(project.Ok);
        Assert.Equal(404, project.Status);
        var group = results.Single(r => r.Target.Kind == GitLabHookTargetKind.Group);
        Assert.False(group.Ok);
        Assert.Equal(403, group.Status);
        Assert.Contains("Premium", group.Detail); // Gruppen-Hooks sind teils Premium-Tier (Spec-Hinweis)
    }

    [Fact]
    public async Task Netzwerkfehler_istErgebnisKeineException()
    {
        var stub = new StubHttpMessageHandler(_ => throw new HttpRequestException("no route to host"));
        var results = await new GitLabHookCreator(new HttpClient(stub)).CreateAsync(
            "https://gitlab.example.com", "t", "https://n.example/webhook/gitlab", "s",
            [new GitLabHookTarget(GitLabHookTargetKind.Project, "1")]);
        Assert.False(results.Single().Ok);
        Assert.Contains("no route to host", results.Single().Detail);
    }
}
```

- [ ] **Step 2: Tests laufen lassen — müssen fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitLabHookCreatorTests`
Expected: FAIL (Compile-Fehler)

- [ ] **Step 3: Implementierung**

```csharp
// src/Naudit.Infrastructure/Setup/GitLabHookCreator.cs
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Naudit.Infrastructure.Setup;

public enum GitLabHookTargetKind { Project, Group }

public sealed record GitLabHookTarget(GitLabHookTargetKind Kind, string IdOrPath);

public sealed record GitLabHookResult(GitLabHookTarget Target, bool Ok, int? Status, string Detail);

/// <summary>Legt GitLab-Webhooks per API an (Wizard-Plattform-Automation). Teilerfolge sind
/// gewollt: jedes Ziel bekommt sein eigenes Ergebnis, es wird nie geworfen. Idempotent:
/// existiert am Ziel schon ein Hook mit derselben URL, wird keiner angelegt (Retry-freundlich).</summary>
public sealed class GitLabHookCreator(HttpClient http)
{
    public async Task<IReadOnlyList<GitLabHookResult>> CreateAsync(string baseUrl, string token,
        string webhookUrl, string secret, IReadOnlyList<GitLabHookTarget> targets, CancellationToken ct = default)
    {
        var results = new List<GitLabHookResult>(targets.Count);
        foreach (var target in targets)
            results.Add(await CreateOneAsync(baseUrl.TrimEnd('/'), token, webhookUrl, secret, target, ct));
        return results;
    }

    private async Task<GitLabHookResult> CreateOneAsync(string baseUrl, string token,
        string webhookUrl, string secret, GitLabHookTarget target, CancellationToken ct)
    {
        var scope = target.Kind == GitLabHookTargetKind.Project ? "projects" : "groups";
        var hooksUrl = $"{baseUrl}/api/v4/{scope}/{Uri.EscapeDataString(target.IdOrPath)}/hooks";
        try
        {
            // Idempotenz-Check: Nicht-200 hier ist egal — dann entscheidet der POST.
            using (var listReq = NewRequest(HttpMethod.Get, hooksUrl, token))
            using (var listRes = await http.SendAsync(listReq, ct))
            {
                if (listRes.StatusCode == HttpStatusCode.OK)
                {
                    var hooks = JsonSerializer.Deserialize<List<HookDto>>(await listRes.Content.ReadAsStringAsync(ct));
                    if (hooks?.Any(h => h.Url == webhookUrl) == true)
                        return new(target, true, 200, "A webhook with this URL already exists — skipped.");
                }
            }

            using var req = NewRequest(HttpMethod.Post, hooksUrl, token);
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                url = webhookUrl,
                token = secret,
                merge_requests_events = true,
                push_events = false,
            }), Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(req, ct);
            return (int)res.StatusCode switch
            {
                201 => new(target, true, 201, "Webhook created."),
                401 or 403 => new(target, false, (int)res.StatusCode,
                    target.Kind == GitLabHookTargetKind.Group
                        ? "Access denied — the token lacks permission (group webhooks may require GitLab Premium)."
                        : "Access denied — the token lacks permission for this project."),
                404 => new(target, false, 404, "Not found — check the ID or path."),
                var s => new(target, false, s, $"GitLab answered {s}."),
            };
        }
        catch (HttpRequestException ex)
        {
            return new(target, false, null, $"Network error: {ex.Message}");
        }
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("PRIVATE-TOKEN", token);
        return req;
    }

    private sealed record HookDto([property: JsonPropertyName("url")] string? Url);
}
```

- [ ] **Step 4: Tests laufen lassen — müssen bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitLabHookCreatorTests`
Expected: PASS (5 Tests)

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Setup/GitLabHookCreator.cs tests/Naudit.Tests/GitLabHookCreatorTests.cs
git commit -m "feat(setup): GitLabHookCreator - Webhook-Anlage per API mit Ergebnis pro Ziel"
```

---

### Task 4: SetupDraft um App-Felder erweitern, Apply schreibt Auth=App

**Files:**
- Modify: `src/Naudit.Web/Endpoints/SetupEndpoints.cs`
- Test: `tests/Naudit.Tests/SetupEndpointTests.cs` (erweitern)

**Interfaces:**
- Consumes: `SetupDraft`/`DraftResponseAsync`/`DraftToSettings` (PR 2), `GitHubManifest.Normalize/ApiBase` (Task 1), `SetupDraftService`.
- Produces: `SetupDraft` mit `GitHubAuth`, `GitHubHost`, `GitHubAppId`, `GitHubAppPrivateKey`, `GitHubAppSlug`, `GitHubManifestState` (**am Ende der Parameterliste**); GET liefert `hasGitHubApp`; Apply-App-Zweig. Grundlage für Task 5/7.

- [ ] **Step 1: Failing Tests ergänzen** (in `SetupEndpointTests`; ggf. Usings `Microsoft.Extensions.DependencyInjection`, `Naudit.Infrastructure.Data`, `Naudit.Infrastructure.Setup`, `Naudit.Web.Endpoints` ergänzen)

```csharp
    [Fact]
    public async Task Draft_appFelder_sindServerseitig_putKannSieNichtSetzen()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));
        // Boeswillig mitgeschickte App-Felder: nur der Manifest-Callback darf sie schreiben.
        await client.PutAsJsonAsync("/api/setup/draft", new
        {
            platform = "GitHub", gitHubAuth = "App",
            gitHubAppId = "1", gitHubAppPrivateKey = "PEM", gitHubAppSlug = "x", gitHubManifestState = "s",
        });
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/draft"));
        Assert.False(doc.RootElement.GetProperty("hasGitHubApp").GetBoolean());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("draft").GetProperty("gitHubAppId").ValueKind);
        // Die Wahl-Felder (Auth/Host) sind dagegen normale Draft-Felder:
        Assert.Equal("App", doc.RootElement.GetProperty("draft").GetProperty("gitHubAuth").GetString());
    }

    /// <summary>Draft mit App-Credentials direkt ueber den Service setzen — im echten Fluss
    /// macht das der Manifest-Callback (Task 5). WithoutGitHubBaseline, sonst sind die
    /// GitHub-Keys env-locked und Apply schreibt sie nicht.</summary>
    private static async Task SeedAppDraftAsync(WebApplicationFactory<Program> factory, string gitHubHost)
    {
        using var scope = factory.Services.CreateScope();
        var drafts = scope.ServiceProvider.GetRequiredService<SetupDraftService>();
        await drafts.SaveAsync(System.Text.Json.JsonSerializer.Serialize(new SetupDraft(
            PublicBaseUrl: "https://naudit.example.com",
            Platform: "GitHub",
            WebhookSecret: "hook-geheim",
            AiProvider: "Ollama", AiModel: "m",
            AccessGateMode: "Open",
            GitHubAuth: "App",
            GitHubHost: gitHubHost,
            GitHubAppId: "4711",
            GitHubAppPrivateKey: "PEM-geheim",
            GitHubAppSlug: "naudit-test")));
    }

    [Fact]
    public async Task Apply_appZweig_schreibtAppSettings_ghesBaseUrl_keinePatKeys()
    {
        using var app = new TestAppFactory().WithoutGitHubBaseline();
        var factory = SetupMode(app);
        var client = await LoggedInAsync(factory);
        await SeedAppDraftAsync(factory, "https://ghes.example.com");

        var res = await client.PostAsync("/api/setup/apply", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        var settings = db.Settings.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal("App", settings["Naudit:GitHub:Auth"]);
        Assert.True(settings.ContainsKey("Naudit:GitHub:App:AppId"));
        Assert.True(settings.ContainsKey("Naudit:GitHub:App:PrivateKey"));
        Assert.True(settings.ContainsKey("Naudit:GitHub:WebhookSecret"));
        Assert.Equal("https://ghes.example.com/api/v3", settings["Naudit:GitHub:BaseUrl"]); // GHES ⇒ API-Base
        Assert.False(settings.ContainsKey("Naudit:GitHub:Token"));                          // kein PAT-Key
        Assert.DoesNotContain("PEM-geheim", settings["Naudit:GitHub:App:PrivateKey"]);      // verschluesselt
    }

    [Fact]
    public async Task Apply_appZweig_githubCom_schreibtKeineBaseUrl()
    {
        using var app = new TestAppFactory().WithoutGitHubBaseline();
        var factory = SetupMode(app);
        var client = await LoggedInAsync(factory);
        await SeedAppDraftAsync(factory, "https://github.com");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/setup/apply", null)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        Assert.DoesNotContain(db.Settings, s => s.Key == "Naudit:GitHub:BaseUrl"); // Options-Default reicht
    }
```

- [ ] **Step 2: Tests laufen lassen — müssen fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupEndpointTests`
Expected: FAIL (Compile-Fehler: neue `SetupDraft`-Parameter existieren nicht)

- [ ] **Step 3: Implementierung** — drei Stellen in `SetupEndpoints.cs`:

(a) `SetupDraft`-Record erweitern (**neue Parameter ans ENDE**):

```csharp
public sealed record SetupDraft(
    string? PublicBaseUrl = null,
    string? Platform = null,          // "GitHub" | "GitLab"
    string? GitToken = null,          // Secret: write-only ueber die API
    string? GitLabBaseUrl = null,
    string? WebhookSecret = null,     // von Naudit generiert bzw. von GitHub (Manifest) — bewusst sichtbar
    string? AiProvider = null,
    string? AiModel = null,
    string? AiEndpoint = null,
    string? AiApiKey = null,          // Secret: write-only ueber die API
    string? AccessGateMode = null,    // "Open" | "Registered"
    string? GitHubAuth = null,        // "Pat" | "App" — Wizard-Wahl, wie Naudit:GitHub:Auth
    string? GitHubHost = null,        // Web-Host (Default https://github.com; GHES: eigener Host)
    string? GitHubAppId = null,       // ab hier serverseitig: nur der Manifest-Callback schreibt
    string? GitHubAppPrivateKey = null, // Secret: nie in GET, nie per PUT setzbar
    string? GitHubAppSlug = null,     // fuer den Install-Link
    string? GitHubManifestState = null); // CSRF-State des Manifest-Flows — nie in GET
```

(b) `DraftResponseAsync`: Maskierung + Flag erweitern:

```csharp
        return Results.Ok(new
        {
            draft = draft with
            {
                GitToken = null, AiApiKey = null,
                GitHubAppPrivateKey = null, GitHubManifestState = null,
            },
            hasGitToken = !string.IsNullOrEmpty(draft.GitToken),
            hasAiApiKey = !string.IsNullOrEmpty(draft.AiApiKey),
            hasGitHubApp = !string.IsNullOrEmpty(draft.GitHubAppId)
                && !string.IsNullOrEmpty(draft.GitHubAppPrivateKey),
        });
```

(c) PUT-Merge: serverseitig verwaltete Felder kommen NIE vom SPA (weder setzen noch löschen); Plattformwechsel verwirft sie wie den GitToken:

```csharp
            var merged = incoming with
            {
                GitToken = !string.IsNullOrEmpty(incoming.GitToken)
                    ? incoming.GitToken : (samePlatform ? existing.GitToken : null),
                AiApiKey = !string.IsNullOrEmpty(incoming.AiApiKey) ? incoming.AiApiKey : existing.AiApiKey,
                // Serverseitig verwaltet (Manifest-Callback): PUT ignoriert eingehende Werte komplett.
                GitHubAppId = samePlatform ? existing.GitHubAppId : null,
                GitHubAppPrivateKey = samePlatform ? existing.GitHubAppPrivateKey : null,
                GitHubAppSlug = samePlatform ? existing.GitHubAppSlug : null,
                GitHubManifestState = samePlatform ? existing.GitHubManifestState : null,
            };
```

(d) `DraftToSettings`: GitHub-Zweig verzweigt nach Auth (bisher hart `"Pat"`):

```csharp
        if (string.Equals(d.Platform, "GitHub", StringComparison.OrdinalIgnoreCase))
        {
            var app = string.Equals(d.GitHubAuth, "App", StringComparison.OrdinalIgnoreCase);
            settings["Naudit:GitHub:Auth"] = app ? "App" : "Pat";
            if (app)
            {
                settings["Naudit:GitHub:App:AppId"] = d.GitHubAppId;
                settings["Naudit:GitHub:App:PrivateKey"] = d.GitHubAppPrivateKey;
                // GHES: API-Base persistieren; github.com bleibt beim Options-Default.
                var host = Naudit.Infrastructure.Setup.GitHubManifest.Normalize(d.GitHubHost);
                if (host != Naudit.Infrastructure.Setup.GitHubManifest.DefaultWebHost)
                    settings["Naudit:GitHub:BaseUrl"] = Naudit.Infrastructure.Setup.GitHubManifest.ApiBase(host);
            }
            else
            {
                settings["Naudit:GitHub:Token"] = d.GitToken;
            }
            settings["Naudit:GitHub:WebhookSecret"] = d.WebhookSecret;
        }
```

Kein zusätzlicher Apply-Guard nötig: `DraftToSettings` emittiert `Auth=App`, und `SetupStatus.Check` über (Draft + env) verlangt dann `App:AppId`/`App:PrivateKey` — fehlen sie, ist Apply wie gehabt 400 mit `missing`.

- [ ] **Step 4: Tests laufen lassen — müssen bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupEndpointTests`
Expected: PASS (alle bisherigen + 3 neue)

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Web/Endpoints/SetupEndpoints.cs tests/Naudit.Tests/SetupEndpointTests.cs
git commit -m "feat(setup): Draft um GitHub-App-Felder erweitert, Apply schreibt Auth=App"
```

---

### Task 5: Manifest-Flow-Endpoints + SetupHttpClientFactory

**Files:**
- Create: `src/Naudit.Web/SetupHttpClientFactory.cs`
- Modify: `src/Naudit.Web/Endpoints/SetupEndpoints.cs`
- Modify: `src/Naudit.Web/Program.cs` (eine Registrierungszeile)
- Test: `tests/Naudit.Tests/SetupEndpointTests.cs` (erweitern)

**Interfaces:**
- Consumes: `GitHubManifest` (Task 1), `GitHubManifestConverter` (Task 2), `SetupDraft` (Task 4), `SetupDraftService`.
- Produces: `POST /api/setup/github/manifest` (Auth-Gruppe) → `{ action, manifest }` + state/host im Draft; `GET /api/setup/github/manifest-callback` (anonym, nur Setup-Modus) → Redirect; `SetupHttpClientFactory(Func<HttpClient> Create)` (auch von Task 6 genutzt).

- [ ] **Step 1: Failing Tests ergänzen** (in `SetupEndpointTests`)

```csharp
    private const string ConversionJson = """
        { "id": 4711, "slug": "naudit-test", "pem": "PEM-geheim",
          "webhook_secret": "hook-geheim", "client_id": "Iv1.x" }
        """;

    [Fact]
    public async Task ManifestFlow_endToEnd_persistiertAppImDraft()
    {
        using var app = new TestAppFactory();
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        { Content = new StringContent(ConversionJson) });
        var factory = SetupMode(app, b => b.ConfigureServices(s =>
            s.AddSingleton(new Naudit.Web.SetupHttpClientFactory(() => new HttpClient(stub)))));
        var client = await LoggedInAsync(factory);
        await client.PutAsJsonAsync("/api/setup/draft", new { publicBaseUrl = "https://naudit.example.com" });

        // 1) Manifest anfordern: action-URL (mit state) + Manifest-JSON fuer den Form-POST.
        var res = await client.PostAsJsonAsync("/api/setup/github/manifest",
            new { org = "my-org", appName = "naudit", @public = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var action = doc.RootElement.GetProperty("action").GetString()!;
        Assert.StartsWith("https://github.com/organizations/my-org/settings/apps/new?state=", action);
        var state = action[(action.IndexOf("state=", StringComparison.Ordinal) + 6)..];
        var manifest = doc.RootElement.GetProperty("manifest");
        Assert.Equal("https://naudit.example.com/webhook/github",
            manifest.GetProperty("hook_attributes").GetProperty("url").GetString());
        Assert.Equal("https://naudit.example.com/api/setup/github/manifest-callback",
            manifest.GetProperty("redirect_url").GetString());
        Assert.True(manifest.GetProperty("public").GetBoolean());

        // 2) Callback: anonym (frischer Client OHNE Cookie), Redirect nicht folgen — Location pruefen.
        var raw = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cb = await raw.GetAsync($"/api/setup/github/manifest-callback?code=code-123&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, cb.StatusCode);
        Assert.Equal("/?setup=github-app-created", cb.Headers.Location!.ToString());
        Assert.Contains("https://api.github.com/app-manifests/code-123/conversions",
            stub.Calls.Select(c => c.Uri!.ToString()));

        // 3) Draft traegt jetzt die App — PEM bleibt maskiert, Secret ist GitHubs generiertes.
        var draftDoc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/draft"));
        Assert.True(draftDoc.RootElement.GetProperty("hasGitHubApp").GetBoolean());
        Assert.Equal("naudit-test", draftDoc.RootElement.GetProperty("draft").GetProperty("gitHubAppSlug").GetString());
        Assert.Equal("4711", draftDoc.RootElement.GetProperty("draft").GetProperty("gitHubAppId").GetString());
        Assert.Equal("hook-geheim", draftDoc.RootElement.GetProperty("draft").GetProperty("webhookSecret").GetString());
        Assert.Equal(JsonValueKind.Null,
            draftDoc.RootElement.GetProperty("draft").GetProperty("gitHubAppPrivateKey").ValueKind);

        // 4) state ist einmal verwendbar: derselbe Callback nochmal ⇒ Fehler-Redirect.
        var replay = await raw.GetAsync($"/api/setup/github/manifest-callback?code=code-123&state={state}");
        Assert.Equal("/?setup=github-app-error&reason=state", replay.Headers.Location!.ToString());
    }

    [Fact]
    public async Task ManifestCallback_falscherState_ruftGitHubNichtUndSetztNichts()
    {
        using var app = new TestAppFactory();
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        { Content = new StringContent(ConversionJson) });
        var factory = SetupMode(app, b => b.ConfigureServices(s =>
            s.AddSingleton(new Naudit.Web.SetupHttpClientFactory(() => new HttpClient(stub)))));
        var client = await LoggedInAsync(factory);
        await client.PutAsJsonAsync("/api/setup/draft", new { publicBaseUrl = "https://naudit.example.com" });
        await client.PostAsJsonAsync("/api/setup/github/manifest", new { @public = false });

        var raw = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cb = await raw.GetAsync("/api/setup/github/manifest-callback?code=c&state=falsch");
        Assert.Equal("/?setup=github-app-error&reason=state", cb.Headers.Location!.ToString());
        Assert.Empty(stub.Calls);
        var draftDoc = JsonDocument.Parse(await client.GetStringAsync("/api/setup/draft"));
        Assert.False(draftDoc.RootElement.GetProperty("hasGitHubApp").GetBoolean());
    }

    [Fact]
    public async Task Manifest_ohneInstanzUrl_ist400()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));
        var res = await client.PostAsJsonAsync("/api/setup/github/manifest", new { @public = false });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task ManifestCallback_fehlgeschlagenerExchange_redirectetMitConversionFehler()
    {
        using var app = new TestAppFactory();
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        { Content = new StringContent("""{"message":"Not Found"}""") });
        var factory = SetupMode(app, b => b.ConfigureServices(s =>
            s.AddSingleton(new Naudit.Web.SetupHttpClientFactory(() => new HttpClient(stub)))));
        var client = await LoggedInAsync(factory);
        await client.PutAsJsonAsync("/api/setup/draft", new { publicBaseUrl = "https://naudit.example.com" });
        var action = JsonDocument.Parse(await (await client.PostAsJsonAsync("/api/setup/github/manifest",
            new { @public = false })).Content.ReadAsStringAsync()).RootElement.GetProperty("action").GetString()!;
        var state = action[(action.IndexOf("state=", StringComparison.Ordinal) + 6)..];

        var raw = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cb = await raw.GetAsync($"/api/setup/github/manifest-callback?code=abgelaufen&state={state}");
        Assert.Equal("/?setup=github-app-error&reason=conversion", cb.Headers.Location!.ToString());
        // Draft und state bleiben — erneuter Versuch generiert ohnehin einen frischen state (Spec: retrybar).
    }
```

- [ ] **Step 2: Tests laufen lassen — müssen fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupEndpointTests`
Expected: FAIL (`SetupHttpClientFactory` existiert nicht / Endpoints 404)

- [ ] **Step 3: Implementierung**

(a) Seam-Record:

```csharp
// src/Naudit.Web/SetupHttpClientFactory.cs
namespace Naudit.Web;

/// <summary>Seam fuer HTTP im Setup-Modus: dort laeuft AddNauditInfrastructure nicht,
/// es gibt keinen IHttpClientFactory. Produktion = new HttpClient() (kurzlebige
/// Wizard-Einmal-Aufrufe), Tests injizieren einen Client mit StubHttpMessageHandler —
/// Muster analog AiTestClientFactory.</summary>
public sealed record SetupHttpClientFactory(Func<HttpClient> Create);
```

(b) Registrierung in `Program.cs`, „Basis immer"-Block, direkt nach der `AiTestClientFactory`-Zeile:

```csharp
    builder.Services.AddSingleton(new SetupHttpClientFactory(() => new HttpClient()));
```

(c) Endpoints in `MapSetupEndpoints` — der **Callback anonym**, direkt nach dem `POST /api/setup/admin`-Mapping (vor der Auth-Gruppe):

```csharp
        // Browser-Redirect von GitHub — bewusst OHNE Cookie-Pflicht: Credential ist der
        // unratbare, an den Draft gebundene, einmal verwendbare state (CSRF). So bricht der
        // Flow nicht, wenn der Cookie den externen Redirect nicht ueberlebt. Antwort ist
        // immer ein Redirect auf die SPA, nie JSON.
        app.MapGet("/api/setup/github/manifest-callback", async (string? code, string? state,
            HttpContext ctx, SetupDraftService drafts, SetupHttpClientFactory httpFactory) =>
        {
            var json = await drafts.LoadAsync(ctx.RequestAborted);
            var draft = json is null ? null : System.Text.Json.JsonSerializer.Deserialize<SetupDraft>(json);
            if (draft is null || string.IsNullOrEmpty(code) || !StateMatches(draft.GitHubManifestState, state))
                return Results.Redirect("/?setup=github-app-error&reason=state");

            try
            {
                using var http = httpFactory.Create();
                var conversion = await new Naudit.Infrastructure.Setup.GitHubManifestConverter(http)
                    .ConvertAsync(draft.GitHubHost, code, ctx.RequestAborted);
                var updated = draft with
                {
                    GitHubAppId = conversion.AppId,
                    GitHubAppPrivateKey = conversion.PrivateKey,
                    WebhookSecret = conversion.WebhookSecret, // GitHubs generiertes Secret ersetzt unseres
                    GitHubAppSlug = conversion.Slug,
                    GitHubManifestState = null,               // einmal verwendbar
                };
                await drafts.SaveAsync(System.Text.Json.JsonSerializer.Serialize(updated), ctx.RequestAborted);
                return Results.Redirect("/?setup=github-app-created");
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                // Draft + state bleiben — Fehler im Schritt, erneut versuchbar (Spec).
                return Results.Redirect("/?setup=github-app-error&reason=conversion");
            }
        });
```

In der Auth-Gruppe (nach `test-ai`):

```csharp
        group.MapPost("/github/manifest", async (GitHubManifestRequest body, HttpContext ctx,
            NauditDbContext db, SetupDraftService drafts) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();

            var json = await drafts.LoadAsync(ctx.RequestAborted);
            var existing = json is null ? new SetupDraft()
                : System.Text.Json.JsonSerializer.Deserialize<SetupDraft>(json)!;
            if (string.IsNullOrWhiteSpace(existing.PublicBaseUrl))
                return Results.BadRequest(new
                { error = "Set the instance URL first — the manifest needs it for the webhook and redirect URLs." });

            // state + host in den Draft: der Callback validiert dagegen und braucht den Host
            // fuer die API-Base (GHES). Plattform-Wahl gleich mit persistieren.
            var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var host = Naudit.Infrastructure.Setup.GitHubManifest.Normalize(body.GitHubHost);
            var updated = existing with
            {
                Platform = "GitHub",
                GitHubAuth = "App",
                GitHubHost = host,
                GitHubManifestState = state,
            };
            await drafts.SaveAsync(System.Text.Json.JsonSerializer.Serialize(updated), ctx.RequestAborted);

            var appName = string.IsNullOrWhiteSpace(body.AppName) ? "naudit" : body.AppName.Trim();
            return Results.Ok(new
            {
                action = Naudit.Infrastructure.Setup.GitHubManifest.CreateAppUrl(host, body.Org, state),
                manifest = Naudit.Infrastructure.Setup.GitHubManifest.Build(
                    existing.PublicBaseUrl, appName, body.Public),
            });
        });
```

Helper (in der Klasse) + DTO (Namespace-Ebene, neben `AiTestRequest`):

```csharp
    /// <summary>Constant-time-Vergleich des CSRF-states (Laengen-Differenz ⇒ false).</summary>
    private static bool StateMatches(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        var e = System.Text.Encoding.UTF8.GetBytes(expected);
        var a = System.Text.Encoding.UTF8.GetBytes(actual);
        return e.Length == a.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(e, a);
    }
```

```csharp
/// <summary>Request des Manifest-Starts. Org/AppName/Public gehen nur an GitHub
/// (nicht persistiert); GitHubHost wandert in den Draft (Callback + Apply brauchen ihn).</summary>
public sealed record GitHubManifestRequest(
    string? GitHubHost = null, string? Org = null, string? AppName = null, bool Public = false);
```

- [ ] **Step 4: Tests laufen lassen — müssen bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupEndpointTests`
Expected: PASS (alle bisherigen + 4 neue)

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Web/SetupHttpClientFactory.cs src/Naudit.Web/Endpoints/SetupEndpoints.cs src/Naudit.Web/Program.cs tests/Naudit.Tests/SetupEndpointTests.cs
git commit -m "feat(setup): Manifest-Flow-Endpoints - Form-POST-Daten + state-gebundener Callback"
```

---

### Task 6: `POST /api/setup/gitlab/hooks`

**Files:**
- Modify: `src/Naudit.Web/Endpoints/SetupEndpoints.cs`
- Test: `tests/Naudit.Tests/SetupEndpointTests.cs` (erweitern)

**Interfaces:**
- Consumes: `GitLabHookCreator` (Task 3), `SetupHttpClientFactory` (Task 5), Draft-Felder `GitLabBaseUrl`/`GitToken`/`WebhookSecret`/`PublicBaseUrl`.
- Produces: `POST /api/setup/gitlab/hooks` `{ projects: string[], groups: string[] }` → `200 { results: [{ target, kind, ok, status, detail }] }`; 400 bei unvollständigem Draft oder ohne Ziele. Von Task 8 (SPA) gerufen.

- [ ] **Step 1: Failing Tests ergänzen**

```csharp
    [Fact]
    public async Task GitLabHooks_ergebnisProZiel_hookUrlAusPublicBaseUrl()
    {
        using var app = new TestAppFactory();
        var stub = new StubHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath.Contains("/groups/")
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") }
                : req.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }
                    : new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") });
        var factory = SetupMode(app, b => b.ConfigureServices(s =>
            s.AddSingleton(new Naudit.Web.SetupHttpClientFactory(() => new HttpClient(stub)))));
        var client = await LoggedInAsync(factory);
        await client.PutAsJsonAsync("/api/setup/draft", new
        {
            platform = "GitLab", gitLabBaseUrl = "https://gitlab.example.com",
            gitToken = "glpat-x", webhookSecret = "hook-1", publicBaseUrl = "https://naudit.example.com",
        });

        var res = await client.PostAsJsonAsync("/api/setup/gitlab/hooks",
            new { projects = new[] { "42" }, groups = new[] { "my-group" } });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var results = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
            .RootElement.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(2, results.Count);
        var project = results.Single(r => r.GetProperty("kind").GetString() == "project");
        Assert.True(project.GetProperty("ok").GetBoolean());
        Assert.Equal("42", project.GetProperty("target").GetString());
        var grp = results.Single(r => r.GetProperty("kind").GetString() == "group");
        Assert.False(grp.GetProperty("ok").GetBoolean());
        Assert.Equal(403, grp.GetProperty("status").GetInt32());
        // Hook-URL kommt aus der PublicBaseUrl, das token-Feld ist das WebhookSecret:
        Assert.Contains(stub.Calls, c => c.Body != null
            && c.Body.Contains("https://naudit.example.com/webhook/gitlab") && c.Body.Contains("hook-1"));
    }

    [Fact]
    public async Task GitLabHooks_ohneDraftDaten_ist400()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));
        var res = await client.PostAsJsonAsync("/api/setup/gitlab/hooks", new { projects = new[] { "1" } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task GitLabHooks_ohneZiele_ist400()
    {
        using var app = new TestAppFactory();
        var client = await LoggedInAsync(SetupMode(app));
        await client.PutAsJsonAsync("/api/setup/draft", new
        {
            platform = "GitLab", gitLabBaseUrl = "https://gitlab.example.com",
            gitToken = "glpat-x", webhookSecret = "s", publicBaseUrl = "https://n.example",
        });
        var res = await client.PostAsJsonAsync("/api/setup/gitlab/hooks",
            new { projects = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
```

- [ ] **Step 2: Tests laufen lassen — müssen fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupEndpointTests`
Expected: FAIL (Endpoint 404)

- [ ] **Step 3: Implementierung** — in der Auth-Gruppe (nach `github/manifest`):

```csharp
        group.MapPost("/gitlab/hooks", async (GitLabHooksRequest body, HttpContext ctx,
            NauditDbContext db, SetupDraftService drafts, SetupHttpClientFactory httpFactory) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();

            var json = await drafts.LoadAsync(ctx.RequestAborted);
            var draft = json is null ? null : System.Text.Json.JsonSerializer.Deserialize<SetupDraft>(json);
            if (draft is null || string.IsNullOrWhiteSpace(draft.GitLabBaseUrl)
                || string.IsNullOrWhiteSpace(draft.GitToken) || string.IsNullOrWhiteSpace(draft.WebhookSecret)
                || string.IsNullOrWhiteSpace(draft.PublicBaseUrl))
                return Results.BadRequest(new
                { error = "Complete the instance URL and GitLab fields first (base URL, token, webhook secret)." });

            var targets = new List<Naudit.Infrastructure.Setup.GitLabHookTarget>();
            foreach (var p in body.Projects ?? [])
                if (!string.IsNullOrWhiteSpace(p))
                    targets.Add(new(Naudit.Infrastructure.Setup.GitLabHookTargetKind.Project, p.Trim()));
            foreach (var g in body.Groups ?? [])
                if (!string.IsNullOrWhiteSpace(g))
                    targets.Add(new(Naudit.Infrastructure.Setup.GitLabHookTargetKind.Group, g.Trim()));
            if (targets.Count == 0)
                return Results.BadRequest(new { error = "Enter at least one project or group." });

            var webhookUrl = $"{draft.PublicBaseUrl.TrimEnd('/')}/webhook/gitlab";
            using var http = httpFactory.Create();
            var results = await new Naudit.Infrastructure.Setup.GitLabHookCreator(http).CreateAsync(
                draft.GitLabBaseUrl, draft.GitToken, webhookUrl, draft.WebhookSecret, targets, ctx.RequestAborted);
            return Results.Ok(new
            {
                results = results.Select(r => new
                {
                    target = r.Target.IdOrPath,
                    kind = r.Target.Kind.ToString().ToLowerInvariant(),
                    ok = r.Ok,
                    status = r.Status,
                    detail = r.Detail,
                }),
            });
        });
```

DTO (Namespace-Ebene):

```csharp
/// <summary>Ziele der GitLab-Webhook-Anlage: Projekt-IDs/-Pfade und Gruppen-IDs/-Pfade.</summary>
public sealed record GitLabHooksRequest(List<string>? Projects = null, List<string>? Groups = null);
```

- [ ] **Step 4: Tests laufen lassen — müssen bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter SetupEndpointTests`
Expected: PASS (alle bisherigen + 3 neue)

- [ ] **Step 5: Voller Backend-Testlauf + Commit**

Run: `dotnet test Naudit.slnx` — Expected: PASS

```bash
git add src/Naudit.Web/Endpoints/SetupEndpoints.cs tests/Naudit.Tests/SetupEndpointTests.cs
git commit -m "feat(setup): POST /api/setup/gitlab/hooks - Webhook-Anlage aus dem Wizard"
```

---

### Task 7: Frontend — GitHub-App-Option, Manifest-Form-POST, Redirect-Resume

**Files:**
- Modify: `src/frontend/src/api/types.ts`
- Modify: `src/frontend/src/components/setup/shared.tsx`
- Modify: `src/frontend/src/components/setup/StepPlatform.tsx`
- Modify: `src/frontend/src/components/setup/SetupWizard.tsx`
- Modify: `src/frontend/src/components/setup/StepSummary.tsx`

**Interfaces:**
- Consumes: `POST /api/setup/github/manifest` (Task 5), erweiterte Draft-API (Task 4), `api`/`ApiError` aus `@/api/client`, `Field`/`CopyRow`/`Button`.
- Produces: `StepPlatform` mit drei Karten und App-Flow; `SetupWizard` mit `saveDraft()` (PUT ohne Weiter), `loadDraft(targetStep)` und `?setup=…`-Resume. Kein Test-Runner im Frontend — Gate ist `npm run lint && npm run build` + manuelle Verifikation (Task 9).

- [ ] **Step 1: Typen erweitern** (`api/types.ts`)

```ts
// SetupDraftDto ergaenzen (PEM/state liefert der Server nie — bewusst nicht im Typ):
  gitHubAuth: "Pat" | "App" | null;
  gitHubHost: string | null;
  gitHubAppId: string | null;
  gitHubAppSlug: string | null;

// SetupDraftResponse ergaenzen:
  hasGitHubApp: boolean;

// Neu:
export interface GitHubManifestResponse {
  action: string;                      // {host}/settings/apps/new?state=… (Form-POST-Ziel)
  manifest: Record<string, unknown>;   // wird als Form-Feld "manifest" mitgeschickt
}
```

- [ ] **Step 2: `shared.tsx` — `WizardDraft`/`emptyDraft` ergänzen**

`gitHubAuth: "" | "Pat" | "App"`, `gitHubHost: string`, `gitHubAppId: string`, `gitHubAppSlug: string` (AppId/Slug sind Anzeige-Felder aus dem GET; das PUT schickt sie mit, der Server ignoriert sie — serverseitig verwaltet).

- [ ] **Step 3: `SetupWizard.tsx` — Save-Extraktion + Redirect-Resume**

1. `saveAndNext()` in `saveDraft()` (nur PUT + Flag-Update) und `saveAndNext()` (ruft `saveDraft`, dann `setStep(s => s+1)`) aufteilen; `saveDraft` als Prop an `StepPlatform` durchreichen (`save`).
2. `loadDraft(targetStep = 1)` — bestehende Logik, aber Ziel-Schritt parametrisiert; Mapping der neuen DTO-Felder (`gitHubAuth` etc. → `""` bei `null`) und `hasGitHubApp` in den State.
3. Rückkehr vom Manifest-Redirect:

```tsx
// Rueckkehr vom GitHub-Manifest-Redirect: /?setup=github-app-created | github-app-error&reason=…
// Query-Param sofort aus der URL entfernen (Reload/Bookmark soll nicht kleben bleiben).
const [manifestReturn] = useState(() => {
  const params = new URLSearchParams(window.location.search);
  const value = params.get("setup");
  if (value) window.history.replaceState(null, "", window.location.pathname);
  return value; // "github-app-created" | "github-app-error" | null
});
const manifestError = manifestReturn === "github-app-error"
  ? "GitHub app creation failed — please try again." : null;

useEffect(() => {
  if (!manifestReturn) return;
  // Session sollte den Redirect ueberlebt haben: direkt zum Plattform-Schritt (Index 2).
  // 401 ⇒ StepAdmin zeigt Login; onDone springt dann ebenfalls zu Schritt 2.
  void loadDraft(2).catch(() => {});
}, []);
```

4. `StepAdmin`-`onDone`: `loadDraft(manifestReturn ? 2 : 1)`.
5. `StepPlatform` bekommt `hasGitHubApp`, `save`, `manifestError` als Props.

- [ ] **Step 4: `StepPlatform.tsx` — drei Karten + App-Pane**

Karten-Modell (Auswahl abgeleitet aus `draft.platform` + `draft.gitHubAuth`):

```tsx
const CHOICES = [
  { key: "GitHubApp", title: "GitHub App", sub: "Recommended — one-click setup" },
  { key: "GitHubPat", title: "GitHub PAT", sub: "Personal access token" },
  { key: "GitLab",    title: "GitLab",     sub: "Self-hosted or gitlab.com" },
] as const;
// selected: platform==="GitLab" ? "GitLab" : gitHubAuth==="App" ? "GitHubApp" : platform==="GitHub" ? "GitHubPat" : null
// pick("GitHubApp") → update({ platform: "GitHub", gitHubAuth: "App" })
// pick("GitHubPat") → update({ platform: "GitHub", gitHubAuth: "Pat", webhookSecret: draft.webhookSecret || randomSecret() })
// pick("GitLab")    → update({ platform: "GitLab", webhookSecret: draft.webhookSecret || randomSecret() })
```

App-Pane (ersetzt den PR-2-Platzhaltertext „One-click app creation is coming next…"):

- Lokaler State: `appName` (Default `"naudit"`), `org` (optional), `isPublic` (Checkbox „Public app — others can install it"), `busy`, `err`. `gitHubHost` liegt im Draft (Feld „GitHub Enterprise host (optional)", Default-Placeholder `https://github.com`).
- Hinweis unter dem Namen: app names are unique on GitHub — you can adjust the name on the GitHub page before creating.
- Wenn `!hasGitHubApp`: Button **„Create GitHub App on GitHub →"** (disabled ohne `draft.publicBaseUrl`; `manifestError` als `text-danger`-Zeile davor):

```tsx
async function createApp() {
  setBusy(true); setErr(null);
  try {
    await save(); // Draft sichern — der Browser verlaesst gleich die Seite
    const res = await api<GitHubManifestResponse>("/api/setup/github/manifest", {
      method: "POST",
      body: JSON.stringify({
        gitHubHost: draft.gitHubHost || null,
        org: org || null,
        appName,
        public: isPublic,
      }),
    });
    // Voller Form-POST zu GitHub (kein fetch — GitHub rendert die Bestaetigungsseite).
    const form = document.createElement("form");
    form.method = "post";
    form.action = res.action;
    const input = document.createElement("input");
    input.type = "hidden";
    input.name = "manifest";
    input.value = JSON.stringify(res.manifest);
    form.appendChild(input);
    document.body.appendChild(form);
    form.submit(); // Rueckkehr via /?setup=github-app-…
  } catch (e) {
    setErr(e instanceof ApiError ? e.message : "Could not start the GitHub app flow.");
    setBusy(false);
  }
}
```

- Wenn `hasGitHubApp`: Erfolgs-Box — „GitHub App created ✓ (app ID {draft.gitHubAppId})", Link
  `{normalizedHost}/apps/{draft.gitHubAppSlug}/installations/new` als **„Install the app on GitHub →"**
  (`target="_blank" rel="noreferrer"`, Markup-Muster: `InstallAppBanner`), Hinweis „Webhook URL and
  secret were configured automatically by GitHub." Kein manueller Webhook-Block in diesem Pfad.
  Sekundär-Button „Create a different app" ruft `createApp()` erneut (frischer state überschreibt).
- Ready-Gate: `GitHubApp` ⇒ `hasGitHubApp`; `GitHubPat`/`GitLab` ⇒ wie bisher (`tokenOk`, GitLab-URL).

- [ ] **Step 5: `StepSummary.tsx`** — Plattform-Zeile zeigt bei `gitHubAuth === "App"` „GitHub App ({gitHubAppSlug})" statt der Token-Zeile.

- [ ] **Step 6: Gate + Commit**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: beide grün

```bash
git add src/frontend/src/api/types.ts src/frontend/src/components/setup/
git commit -m "feat(setup): Wizard-Plattform-Schritt mit GitHub-App-Option und Manifest-Redirect"
```

---

### Task 8: Frontend — GitLab-Webhook-Anlage im Wizard

**Files:**
- Modify: `src/frontend/src/api/types.ts`
- Modify: `src/frontend/src/components/setup/StepPlatform.tsx`

**Interfaces:**
- Consumes: `POST /api/setup/gitlab/hooks` (Task 6), `save`-Prop (Task 7), `Pill` (`@/components/ui/Pill`).
- Produces: GitLab-Pane mit „Create webhooks automatically"-Block + Ergebniszeile pro Ziel; manueller Copy-Paste-Pfad bleibt darunter.

- [ ] **Step 1: Typen** (`api/types.ts`)

```ts
export interface GitLabHookResultDto {
  target: string;
  kind: "project" | "group";
  ok: boolean;
  status: number | null;
  detail: string;
}
export interface GitLabHooksResponse { results: GitLabHookResultDto[]; }
```

- [ ] **Step 2: GitLab-Pane erweitern** (`StepPlatform.tsx`)

Lokaler State: `projects` (Textarea „Project IDs or paths — one per line"), `groups` (Input „Groups (optional; may require GitLab Premium)"), `hookResults: GitLabHookResultDto[] | null`, `creating`.

```tsx
const splitTargets = (raw: string) =>
  raw.split(/[\n,]/).map((s) => s.trim()).filter(Boolean);

async function createHooks() {
  setCreating(true); setErr(null);
  try {
    await save(); // der Endpoint liest Token/Secret/URLs aus dem Draft
    const res = await api<GitLabHooksResponse>("/api/setup/gitlab/hooks", {
      method: "POST",
      body: JSON.stringify({ projects: splitTargets(projects), groups: splitTargets(groups) }),
    });
    setHookResults(res.results);
  } catch (e) {
    setErr(e instanceof ApiError ? e.message : "Webhook creation failed.");
  } finally {
    setCreating(false);
  }
}
```

- Button **„Create webhooks"** (`loading={creating}`, disabled ohne Token/PublicBaseUrl oder ohne Ziele).
- Ergebnisliste: pro Zeile `<Pill kind={r.ok ? "ok" : "danger"}>` + `{r.target}` + `{r.detail}` (`font-mono text-xs`).
- Der Block ist optional/non-blocking: „Continue" hängt wie bisher nur an Token + URL; darunter bleibt der manuelle Pfad („Or add the webhook manually:" + die zwei `CopyRow`s aus PR 2).

- [ ] **Step 3: Gate + Commit**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: beide grün

```bash
git add src/frontend/src/api/types.ts src/frontend/src/components/setup/StepPlatform.tsx
git commit -m "feat(setup): GitLab-Webhook-Anlage im Wizard mit Ergebnis pro Ziel"
```

---

### Task 9: Docs, Spec-Rückschreibung, manueller Smoke-Test

**Files:**
- Modify: `docs/github-app.md`, `docs/getting-started.md`, `docs/webui.md`, `docs/configuration.md`, `CLAUDE.md`, `docs/superpowers/specs/2026-07-08-setup-wizard-design.md`

- [ ] **Step 1: Docs aktualisieren** (englisch)

- `docs/github-app.md`: neue Sektion „Automatic setup (wizard)" — Manifest-Flow als empfohlener Weg (ein Klick, Credentials landen automatisch in der Config), manuelle App-Erstellung bleibt dokumentiert; GHES-Hinweis (API-Base `/api/v3` wird automatisch gesetzt; Install-Link des Onboarding-Banners aktuell nur github.com).
- `docs/getting-started.md`: Wizard-Beschreibung um „GitHub App (recommended, one-click)" und die GitLab-Webhook-Automatik ergänzen.
- `docs/webui.md`: Wizard-Plattform-Schritt = drei Optionen; Callback-/state-Design kurz erklären (anonym, state-gebunden, einmal verwendbar).
- `docs/configuration.md`: Hinweis, dass der Wizard bei GHES `Naudit:GitHub:BaseUrl` automatisch setzt.
- `CLAUDE.md`: den Satz „Only the **platform automation** … (PR 3) remains outstanding" ersetzen — Wizard inkl. Plattform-Automation ist komplett; die neuen Endpoints (`/api/setup/github/manifest`, `…/manifest-callback`, `…/gitlab/hooks`) und die `SetupHttpClientFactory`-Seam in einem Satz erwähnen.

- [ ] **Step 2: Spec-Präzisierungen zurückschreiben** (Abschnitt „Plattform-Automation" ergänzen)

1. Der Manifest-Callback ist **anonym** und state-gebunden (einmal verwendbar, constant-time) — bewusst kein Cookie-Zwang, damit der externe Redirect nicht an Cookie-Verlust scheitert.
2. Org, App-Name und Public-Flag werden **nicht persistiert** (gehen nur an GitHub); der App-**Slug** lebt nur im Draft (Install-Link), nicht in den Settings.
3. GitLab-Hook-Anlage ist **idempotent** (vorhandene Hook-URL ⇒ skip statt Duplikat).
4. GHES: Apply setzt `Naudit:GitHub:BaseUrl = {host}/api/v3`; Limitation Install-Link im Onboarding-Banner (hart github.com) bleibt.

- [ ] **Step 3: Volle Gates**

Run: `dotnet test Naudit.slnx` und `cd src/frontend && npm run lint && npm run build`
Expected: alles grün

- [ ] **Step 4: Manueller Smoke-Test** (dokumentieren, was geprüft wurde)

1. Frische DB, `dotnet run --project src/Naudit.Web` → Wizard → Admin anlegen → Instanz-URL (die spätere öffentliche URL) → **GitHub App** → „Create GitHub App on GitHub" → auf github.com bestätigen → Rückkehr: Wizard steht auf dem Plattform-Schritt, „GitHub App created ✓", Install-Link öffnet die Installationsseite → Wizard zu Ende → Apply → Neustart → Status konfiguriert (Auth=App in den Settings sichtbar, PrivateKey „gesetzt"). Wegwerf-App danach auf GitHub löschen.
2. GitLab-Pfad gegen gitlab.com-Testprojekt: Token + Projekt-ID → „Create webhooks" → Ergebnis „Webhook created." → im GitLab-Projekt sichtbar (MR-Events, Secret gesetzt); zweiter Klick ⇒ „already exists — skipped."
3. Fehlerpfad: Callback mit manipuliertem `state` in der URL ⇒ Wizard zeigt den Fehler, erneuter Versuch funktioniert.

- [ ] **Step 5: Commit**

```bash
git add docs/ CLAUDE.md
git commit -m "docs(setup): Plattform-Automation dokumentiert, Spec-Rueckschreibung"
```

---

## Out of Scope (bewusst nicht in diesem PR)

- **„Verbindung testen" auf der Settings-Seite** (Git-Token-Check GitLab/GitHub `GET /user`, App-JWT `GET /app`) — eigener Follow-up; die Hook-Anlage validiert den GitLab-Token implizit, der Manifest-Flow braucht keinen Token.
- GHES-fähiger Install-Link im Onboarding-Banner (`GitHubAppInstallationChecker` hardcodet github.com).
- Persistenz von App-Slug/Org/Public-Flag über den Draft hinaus.
