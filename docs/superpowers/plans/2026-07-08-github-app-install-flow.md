# GitHub App Installation in the Sign-in Flow — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** After signing in, the WebUI leads the user to install the Naudit GitHub App on their account/org via an onboarding banner that knows the real installation state.

**Architecture:** A new `GitHubAppInstallationChecker` (Infrastructure) mints an App JWT — via a shared `GitHubAppJwt` extracted from the existing token provider — and asks GitHub per linked login whether the app is installed (`GET /users/{login}/installation`, org fallback), with the install deep-link built from the app slug (`GET /app`). A gated `GET /api/me/github-app` endpoint exposes this to the SPA, which renders an install banner on the Dashboard and PendingPage and a status row on the Profile page. Everything is registered/mapped only on GitHub + `Auth=App` + UI deployments; on any other deployment the endpoint 404s and the banner stays hidden.

**Tech Stack:** .NET 10 / ASP.NET Minimal API, EF Core, xUnit; React 19 + TypeScript + TanStack Query + Tailwind 4 (Vite).

## Global Constraints

- **Core rule:** `Naudit.Core` depends only on `Microsoft.Extensions.AI.Abstractions`. This feature adds **no** Core types — everything lives in `Naudit.Infrastructure` (checker) and `Naudit.Web` (endpoint). Do not touch Core.
- **Solution file is `Naudit.slnx`** (not `.sln`). `dotnet test Naudit.sln` fails with MSB1009.
- **Code comments in German.** Docs and `README`/`docs/` in English.
- **Secrets never in `appsettings.json`** — user-secrets/env only. Never log the private key or any token.
- **Feature gating:** the checker (DI) and the endpoint (mapping) exist **only** when `Naudit:Git:Platform=GitHub` **and** `Naudit:GitHub:Auth=App` **and** `Naudit:Ui:Enabled=true`. Same "not enabled ⇒ route not mapped ⇒ 404" pattern as the opt-in auth routes.
- **Fail-quiet:** any GitHub API failure in the checker yields `installed: null` (logged as a warning); it never throws to the endpoint and never affects reviews.
- **Frontend build gate:** `cd src/frontend && npm run lint && npm run build` must pass (build = `tsc --noEmit && vite build`).
- **Branch:** work on `feat/github-app-install-flow` (already created off `origin/main`). The design spec lives at `docs/superpowers/specs/2026-07-08-github-app-install-flow-design.md`.

---

### Task 1: Extract `GitHubAppJwt` (shared App-JWT signer)

Pull the RS256 App-JWT minting out of `GitHubAppTokenProvider` into a small, thread-safe, reusable class so the new installation checker can reuse it. The existing token-provider tests are the safety net that the refactor changed no behaviour.

**Files:**
- Create: `src/Naudit.Infrastructure/Git/GitHub/GitHubAppJwt.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubAppTokenProvider.cs` (replace inline JWT/RSA code with the new class)
- Test: `tests/Naudit.Tests/GitHubAppJwtTests.cs` (new), plus the existing `tests/Naudit.Tests/GitHubAppTokenProviderTests.cs` (must still pass unchanged)

**Interfaces:**
- Produces: `Naudit.Infrastructure.Git.GitHub.GitHubAppJwt` — `public GitHubAppJwt(string appId, string privateKey, TimeProvider? time = null)`, `public string Create()` (returns a signed `header.payload.signature` JWT string), `IDisposable`. `privateKey` accepts raw PEM or base64-encoded PEM.
- Consumes: nothing (leaf).

- [ ] **Step 1: Write the failing test**

Create `tests/Naudit.Tests/GitHubAppJwtTests.cs`:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Naudit.Infrastructure.Git.GitHub;
using Xunit;

namespace Naudit.Tests;

public class GitHubAppJwtTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void Create_producesVerifiableRs256Jwt_withIssuerAndExpiry()
    {
        using var rsa = RSA.Create(2048);
        using var jwt = new GitHubAppJwt("12345", rsa.ExportRSAPrivateKeyPem(), new FakeTime(T0));

        var token = jwt.Create();

        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1]));
        Assert.Contains("\"iss\":\"12345\"", payload);
        // exp = now + 9 min (< GitHub-Maximum 10 min), iat = now - 60 s.
        Assert.Contains($"\"exp\":{T0.AddMinutes(9).ToUnixTimeSeconds()}", payload);
        Assert.Contains($"\"iat\":{T0.AddSeconds(-60).ToUnixTimeSeconds()}", payload);
        // Echte RS256-Signatur mit dem Public Key verifizieren.
        Assert.True(rsa.VerifyData(
            Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Create_acceptsBase64EncodedPem()
    {
        using var rsa = RSA.Create(2048);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportRSAPrivateKeyPem()));
        using var jwt = new GitHubAppJwt("12345", b64, new FakeTime(T0));

        var parts = jwt.Create().Split('.');
        Assert.True(rsa.VerifyData(
            Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubAppJwtTests`
Expected: FAIL to compile — `GitHubAppJwt` does not exist.

- [ ] **Step 3: Create `GitHubAppJwt`**

Create `src/Naudit.Infrastructure/Git/GitHub/GitHubAppJwt.cs`:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Naudit.Infrastructure.Git.GitHub;

/// <summary>Erzeugt kurzlebige App-JWTs (RS256) für die GitHub-App-Authentifizierung. Von
/// <see cref="GitHubAppTokenProvider"/> und <see cref="GitHubAppInstallationChecker"/> geteilt.
/// Der private Key wird EINMAL importiert; das Signieren läuft unter einem Lock (RSA-Instanzmethoden
/// sind nicht garantiert thread-safe). Key und JWT dürfen NIE geloggt werden.</summary>
public sealed class GitHubAppJwt : IDisposable
{
    private readonly RSA _rsa;
    private readonly string _appId;
    private readonly TimeProvider _time;
    private readonly object _sign = new();

    public GitHubAppJwt(string appId, string privateKey, TimeProvider? time = null)
    {
        _appId = appId;
        _rsa = ImportPrivateKey(privateKey);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>App-JWT: RS256, iat 60 s rückdatiert (Clock-Skew), exp 9 min (&lt; GitHub-Maximum 10 min).</summary>
    public string Create()
    {
        var now = _time.GetUtcNow();
        var header = """{"alg":"RS256","typ":"JWT"}""";
        var payload = $$"""{"iat":{{now.AddSeconds(-60).ToUnixTimeSeconds()}},"exp":{{now.AddMinutes(9).ToUnixTimeSeconds()}},"iss":"{{_appId}}"}""";
        var signingInput = $"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header))}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))}";
        byte[] signature;
        lock (_sign)
            signature = _rsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    private static RSA ImportPrivateKey(string key)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(LoadPem(key));
        return rsa;
    }

    // Env-freundlich: roher PEM-Text ODER Base64-codiertes PEM (Coolify/Docker-Env ohne Zeilenumbrüche).
    private static string LoadPem(string key)
        => key.Contains("-----BEGIN", StringComparison.Ordinal)
            ? key
            : Encoding.UTF8.GetString(Convert.FromBase64String(key));

    public void Dispose() => _rsa.Dispose();
}
```

- [ ] **Step 4: Run the new test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubAppJwtTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Refactor `GitHubAppTokenProvider` to use `GitHubAppJwt`**

In `src/Naudit.Infrastructure/Git/GitHub/GitHubAppTokenProvider.cs`:

Replace the field block (the `_rsa` field and its comment) — currently:

```csharp
    private readonly SemaphoreSlim _gate = new(1, 1);                          // serialisiert nur Lookup/Mint, nicht den Cache-Hit
    // Key einmal importieren (er ändert sich nie); signiert wird nur unter _gate,
    // weil RSA-Instanzmethoden nicht thread-safe garantiert sind.
    private readonly RSA _rsa = ImportPrivateKey(options.PrivateKey);
```

with:

```csharp
    private readonly SemaphoreSlim _gate = new(1, 1);                          // serialisiert nur Lookup/Mint, nicht den Cache-Hit
    // JWT-Signieren ist in GitHubAppJwt gekapselt (eigener Lock); der Key wird dort einmal importiert.
    private readonly GitHubAppJwt _jwt = new(options.AppId, options.PrivateKey, time);
```

Replace the `CreateJwt()` call inside `SendWithJwtAsync` — currently:

```csharp
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateJwt());
```

with:

```csharp
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _jwt.Create());
```

Delete the now-unused `CreateJwt()` method, the `ImportPrivateKey` method, and the `LoadPem` method (all three moved into `GitHubAppJwt`).

Update `Dispose()` — currently:

```csharp
    public void Dispose()
    {
        _rsa.Dispose();
        _gate.Dispose();
    }
```

to:

```csharp
    public void Dispose()
    {
        _jwt.Dispose();
        _gate.Dispose();
    }
```

Remove now-unused `using` directives if the build warns (`System.Buffers.Text`, `System.Security.Cryptography` — only if no longer referenced elsewhere in the file; `System.Text` may still be needed — let the compiler guide you).

- [ ] **Step 6: Run the full GitHub App token + JWT tests to verify the refactor is behaviour-preserving**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~GitHubApp"`
Expected: PASS — all `GitHubAppTokenProviderTests` (8) and `GitHubAppJwtTests` (2) green. The token-provider tests verify the JWT signature end-to-end, so a green run proves the extraction preserved behaviour.

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Infrastructure/Git/GitHub/GitHubAppJwt.cs \
        src/Naudit.Infrastructure/Git/GitHub/GitHubAppTokenProvider.cs \
        tests/Naudit.Tests/GitHubAppJwtTests.cs
git commit -m "refactor(github-app): App-JWT-Erzeugung in wiederverwendbares GitHubAppJwt auslagern"
```

---

### Task 2: `GitHubAppInstallationChecker`

The unit that asks GitHub whether the app is installed for each linked login and builds the install deep-link. Fail-quiet, cached, org-fallback.

**Files:**
- Create: `src/Naudit.Infrastructure/Git/GitHub/GitHubAppInstallationChecker.cs` (interface + records + implementation)
- Test: `tests/Naudit.Tests/GitHubAppInstallationCheckerTests.cs`

**Interfaces:**
- Consumes: `GitHubAppJwt` (Task 1), an `HttpClient` whose `BaseAddress` is the GitHub API base (`https://api.github.com/`).
- Produces:
  - `public interface IGitHubAppInstallationChecker { ValueTask<GitHubInstallationStatus> GetStatusAsync(IReadOnlyList<string> logins, CancellationToken ct = default); }`
  - `public sealed record GitHubInstallationStatus(string InstallUrl, IReadOnlyList<GitHubLoginInstallation> Accounts);`
  - `public sealed record GitHubLoginInstallation(string Login, bool? Installed);` — `Installed`: `true` installed, `false` not installed, `null` check failed.

- [ ] **Step 1: Write the failing tests**

Create `tests/Naudit.Tests/GitHubAppInstallationCheckerTests.cs`:

```csharp
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitHubAppInstallationCheckerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static GitHubAppInstallationChecker Checker(StubHttpMessageHandler stub, TimeProvider time)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var jwt = new GitHubAppJwt("12345", rsa.ExportRSAPrivateKeyPem(), time);
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.github.com/") };
        return new GitHubAppInstallationChecker(http, jwt, NullLogger<GitHubAppInstallationChecker>.Instance, time);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Stub: GET /app → slug; GET /users/{login}/installation und /orgs/{login}/installation je nach Login.
    private static StubHttpMessageHandler Api(Func<string, HttpResponseMessage> installation)
        => new(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/app") return Json(HttpStatusCode.OK, """{"slug":"naudit"}""");
            return installation(path);
        });

    [Fact]
    public async Task GetStatusAsync_userInstalled_returnsInstalledTrue_andDeepLink()
    {
        var stub = Api(path => path.StartsWith("/users/") ? Json(HttpStatusCode.OK, """{"id":1}""") : new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = Checker(stub, new FakeTime(T0));

        var status = await checker.GetStatusAsync(["octocat"]);

        Assert.Equal("https://github.com/apps/naudit/installations/new", status.InstallUrl);
        var acct = Assert.Single(status.Accounts);
        Assert.Equal("octocat", acct.Login);
        Assert.True(acct.Installed);
    }

    [Fact]
    public async Task GetStatusAsync_notUserButOrgInstalled_fallsBackToOrg_returnsTrue()
    {
        var stub = Api(path =>
            path.StartsWith("/users/") ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : path.StartsWith("/orgs/") ? Json(HttpStatusCode.OK, """{"id":2}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = Checker(stub, new FakeTime(T0));

        var status = await checker.GetStatusAsync(["my-org"]);

        Assert.True(Assert.Single(status.Accounts).Installed);
        Assert.Contains(stub.Calls, c => c.Uri!.AbsolutePath == "/orgs/my-org/installation");
    }

    [Fact]
    public async Task GetStatusAsync_notInstalledAnywhere_returnsFalse()
    {
        var stub = Api(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var checker = Checker(stub, new FakeTime(T0));

        Assert.False(Assert.Single((await checker.GetStatusAsync(["nobody"])).Accounts).Installed);
    }

    [Fact]
    public async Task GetStatusAsync_apiError_returnsNull_failQuiet()
    {
        var stub = Api(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var checker = Checker(stub, new FakeTime(T0));

        Assert.Null(Assert.Single((await checker.GetStatusAsync(["octocat"])).Accounts).Installed);
    }

    [Fact]
    public async Task GetStatusAsync_cachesResultAndSlug_withinTtl()
    {
        var stub = Api(path => path.StartsWith("/users/") ? Json(HttpStatusCode.OK, """{"id":1}""") : new HttpResponseMessage(HttpStatusCode.NotFound));
        var time = new FakeTime(T0);
        var checker = Checker(stub, time);

        await checker.GetStatusAsync(["octocat"]);
        var callsAfterFirst = stub.Calls.Count;  // /app + /users/octocat/installation = 2
        await checker.GetStatusAsync(["octocat"]);

        // 2. Aufruf innerhalb der TTL: kein weiterer HTTP-Call (Slug + Ergebnis gecached).
        Assert.Equal(2, callsAfterFirst);
        Assert.Equal(2, stub.Calls.Count);
    }

    [Fact]
    public async Task GetStatusAsync_errorNotCached_retriedOnNextCall()
    {
        var fail = true;
        var stub = new StubHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/app") return Json(HttpStatusCode.OK, """{"slug":"naudit"}""");
            if (fail) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return path.StartsWith("/users/") ? Json(HttpStatusCode.OK, """{"id":1}""") : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var checker = Checker(stub, new FakeTime(T0));

        Assert.Null(Assert.Single((await checker.GetStatusAsync(["octocat"])).Accounts).Installed);
        fail = false;
        // Fehler wurde NICHT gecached ⇒ erneuter Probe-Call, jetzt installiert.
        Assert.True(Assert.Single((await checker.GetStatusAsync(["octocat"])).Accounts).Installed);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubAppInstallationCheckerTests`
Expected: FAIL to compile — `GitHubAppInstallationChecker` / `IGitHubAppInstallationChecker` do not exist.

- [ ] **Step 3: Create `GitHubAppInstallationChecker`**

Create `src/Naudit.Infrastructure/Git/GitHub/GitHubAppInstallationChecker.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Naudit.Infrastructure.Git.GitHub;

/// <summary>Prüft für das WebUI-Onboarding, ob die Naudit-GitHub-App bei den verknüpften Logins
/// installiert ist, und liefert den Deep-Link zur Installation. Fail-quiet: API-Fehler ⇒ null
/// (Banner bleibt aus), nie werfend — der Review-Betrieb ist davon nie betroffen.</summary>
public interface IGitHubAppInstallationChecker
{
    ValueTask<GitHubInstallationStatus> GetStatusAsync(IReadOnlyList<string> logins, CancellationToken ct = default);
}

/// <summary>Deep-Link zur App-Installation + je Login der Status (null = nicht ermittelbar).</summary>
public sealed record GitHubInstallationStatus(string InstallUrl, IReadOnlyList<GitHubLoginInstallation> Accounts);

/// <summary><paramref name="Installed"/>: true = installiert, false = nicht installiert, null = Prüfung fehlgeschlagen.</summary>
public sealed record GitHubLoginInstallation(string Login, bool? Installed);

public sealed class GitHubAppInstallationChecker(
    HttpClient http, GitHubAppJwt jwt, ILogger<GitHubAppInstallationChecker> logger, TimeProvider? time = null)
    : IGitHubAppInstallationChecker
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, (bool? Installed, DateTimeOffset ExpiresAt)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private volatile string? _slug;   // Slug ändert sich nie ⇒ Prozess-Lebensdauer cachen.

    public async ValueTask<GitHubInstallationStatus> GetStatusAsync(IReadOnlyList<string> logins, CancellationToken ct = default)
    {
        var slug = await GetSlugAsync(ct);
        var installUrl = slug is null ? "" : $"https://github.com/apps/{slug}/installations/new";
        var accounts = new List<GitHubLoginInstallation>(logins.Count);
        foreach (var login in logins)
            accounts.Add(new GitHubLoginInstallation(login, await IsInstalledAsync(login, ct)));
        return new GitHubInstallationStatus(installUrl, accounts);
    }

    private async ValueTask<bool?> IsInstalledAsync(string login, CancellationToken ct)
    {
        if (_cache.TryGetValue(login, out var c) && _time.GetUtcNow() < c.ExpiresAt)
            return c.Installed;
        var installed = await ProbeAsync(login, ct);
        // Nur echte Ergebnisse cachen; Fehler (null) beim nächsten Laden erneut versuchen.
        if (installed is not null)
            _cache[login] = (installed, _time.GetUtcNow() + CacheFor);
        return installed;
    }

    // GET /users/{login}/installation → 200 installiert / 404 nicht ⇒ dann Org-Fallback probieren.
    private async ValueTask<bool?> ProbeAsync(string login, CancellationToken ct)
    {
        try
        {
            using var userResp = await SendAsync($"users/{login}/installation", ct);
            if (userResp.StatusCode == HttpStatusCode.OK) return true;
            if (userResp.StatusCode != HttpStatusCode.NotFound) return LogAndNull(login, userResp.StatusCode);

            using var orgResp = await SendAsync($"orgs/{login}/installation", ct);
            if (orgResp.StatusCode == HttpStatusCode.OK) return true;
            if (orgResp.StatusCode == HttpStatusCode.NotFound) return false;
            return LogAndNull(login, orgResp.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "GitHub-App-Installationsprüfung für {Login} fehlgeschlagen.", login);
            return null;
        }
    }

    private async ValueTask<string?> GetSlugAsync(CancellationToken ct)
    {
        if (_slug is not null) return _slug;
        try
        {
            using var resp = await SendAsync("app", ct);
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                logger.LogWarning("GitHub-App-Slug-Abruf: unerwarteter Status {Status}.", (int)resp.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var slug = doc.RootElement.TryGetProperty("slug", out var s) ? s.GetString() : null;
            if (!string.IsNullOrEmpty(slug)) _slug = slug;
            return _slug;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "GitHub-App-Slug konnte nicht geladen werden.");
            return null;
        }
    }

    private bool? LogAndNull(string login, HttpStatusCode code)
    {
        logger.LogWarning("GitHub-App-Installationsprüfung für {Login}: unerwarteter Status {Status}.", login, (int)code);
        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt.Create());
        return await http.SendAsync(req, ct);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubAppInstallationCheckerTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Git/GitHub/GitHubAppInstallationChecker.cs \
        tests/Naudit.Tests/GitHubAppInstallationCheckerTests.cs
git commit -m "feat(github-app): Installations-Checker (App-JWT, User/Org-Lookup, fail-quiet, gecached)"
```

---

### Task 3: DI wiring + `GET /api/me/github-app` endpoint

Register the checker in the GitHub + `Auth=App` branch and expose it over a gated endpoint the SPA calls.

**Files:**
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (register `GitHubAppJwt` + `IGitHubAppInstallationChecker` in the `Auth == App` block)
- Create: `src/Naudit.Web/Endpoints/GitHubAppEndpoints.cs`
- Modify: `src/Naudit.Web/Program.cs` (map the endpoint inside the `uiConfig.Enabled` block)
- Test: `tests/Naudit.Tests/GitHubAppEndpointTests.cs`

**Interfaces:**
- Consumes: `IGitHubAppInstallationChecker` / `GitHubInstallationStatus` / `GitHubLoginInstallation` (Task 2), `GitHubAppJwt` (Task 1), `GitOptions` + `GitHubOptions` (existing), `CurrentAccount` + `NauditDbContext` (existing).
- Produces: `GET /api/me/github-app` returning `{ installUrl: string, accounts: [{ login: string, installed: bool | null }] }`; mapped only when `Platform=GitHub && Auth=App && Ui:Enabled`. Extension method `MapGitHubAppEndpoints(this WebApplication app, GitOptions git, GitHubOptions gitHub)`.

- [ ] **Step 1: Write the failing endpoint tests**

Create `tests/Naudit.Tests/GitHubAppEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure.Git.GitHub;
using Xunit;

namespace Naudit.Tests;

public class GitHubAppEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public GitHubAppEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private sealed class FakeChecker : IGitHubAppInstallationChecker
    {
        public ValueTask<GitHubInstallationStatus> GetStatusAsync(IReadOnlyList<string> logins, CancellationToken ct = default)
            => ValueTask.FromResult(new GitHubInstallationStatus(
                "https://github.com/apps/naudit/installations/new",
                [new GitHubLoginInstallation("octocat", false)]));
    }

    // App-Modus verlangt einen echten Private Key (Fail-fast in AddNauditInfrastructure).
    private static string TestPem()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private WebApplicationFactory<Program> App(bool authApp, bool withFakeChecker = true)
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-ghapp-{Guid.NewGuid():N}.db")}";
        return _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitHub");
            b.UseSetting("Naudit:GitHub:WebhookSecret", "s");
            b.UseSetting("Naudit:Ai:Provider", "Ollama");
            b.UseSetting("Naudit:Ai:Model", "llama3.1");
            b.UseSetting("Naudit:Ui:Enabled", "true");
            b.UseSetting("Naudit:Db:Enabled", "true");
            b.UseSetting("Naudit:Db:ConnectionString", db);
            b.UseSetting("Naudit:Ui:Admin:Username", "root");
            b.UseSetting("Naudit:Ui:Admin:InitialPassword", "passwort123");
            if (authApp)
            {
                b.UseSetting("Naudit:GitHub:Auth", "App");
                b.UseSetting("Naudit:GitHub:App:AppId", "12345");
                b.UseSetting("Naudit:GitHub:App:PrivateKey", TestPem());
            }
            if (withFakeChecker)
                b.ConfigureTestServices(s => s.AddSingleton<IGitHubAppInstallationChecker>(new FakeChecker()));
        });
    }

    private static async Task<HttpClient> LoggedIn(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateDefaultClient(new CookieContainerHandler());
        await client.PostAsJsonAsync("/auth/login", new { username = "root", password = "passwort123" });
        return client;
    }

    [Fact]
    public async Task GitHubApp_returnsInstallStatus_whenAuthApp()
    {
        var client = await LoggedIn(App(authApp: true));
        var res = await client.GetAsync("/api/me/github-app");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://github.com/apps/naudit/installations/new", body.GetProperty("installUrl").GetString());
        var first = body.GetProperty("accounts")[0];
        Assert.Equal("octocat", first.GetProperty("login").GetString());
        Assert.False(first.GetProperty("installed").GetBoolean());
    }

    [Fact]
    public async Task GitHubApp_notMapped_whenAuthIsPat()
    {
        var client = await LoggedIn(App(authApp: false, withFakeChecker: false));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/me/github-app")).StatusCode);
    }

    [Fact]
    public async Task GitHubApp_requires401ForAnonymous()
    {
        var client = App(authApp: true).CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me/github-app")).StatusCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubAppEndpointTests`
Expected: FAIL — the endpoint is not mapped (`GitHubApp_returnsInstallStatus...` gets 404; the anonymous test may pass incidentally). At least one assertion fails / compile error on `MapGitHubAppEndpoints` if referenced.

- [ ] **Step 3: Register the checker in DI**

In `src/Naudit.Infrastructure/DependencyInjection.cs`, inside the `if (gitHubOptions.Auth == GitHubAuthKind.App)` block, after the existing `services.AddSingleton<IGitTokenProvider>(...)` registration and before the closing `}`, add:

```csharp
                    // Installations-Checker fürs WebUI-Onboarding: eigener App-JWT (gleiche Basis wie
                    // der Token-Provider), gleicher named Client. Singleton, weil Slug/Ergebnisse gecached werden.
                    var appJwt = new GitHubAppJwt(gitHubOptions.App.AppId, gitHubOptions.App.PrivateKey);
                    services.AddSingleton(appJwt);
                    services.AddSingleton<IGitHubAppInstallationChecker>(sp => new GitHubAppInstallationChecker(
                        sp.GetRequiredService<IHttpClientFactory>().CreateClient("github-app"),
                        appJwt,
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<GitHubAppInstallationChecker>()));
```

(The `github-app` named `HttpClient` is already registered a few lines above via `services.AddHttpClient("github-app", ...)`.)

- [ ] **Step 4: Create the endpoint**

Create `src/Naudit.Web/Endpoints/GitHubAppEndpoints.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;

namespace Naudit.Web.Endpoints;

/// <summary>Installations-Status der Naudit-GitHub-App für den eingeloggten User (Onboarding-Banner).
/// Nur gemappt, wenn Plattform=GitHub UND Auth=App — sonst existiert die Route nicht (404), und das
/// SPA zeigt kein Banner. Auch für Pending-Accounts erreichbar: das Banner soll gerade in der
/// Wartezeit erscheinen.</summary>
public static class GitHubAppEndpoints
{
    public static void MapGitHubAppEndpoints(this WebApplication app, GitOptions git, GitHubOptions gitHub)
    {
        if (git.Platform != GitPlatformKind.GitHub || gitHub.Auth != GitHubAuthKind.App)
            return;

        app.MapGet("/api/me/github-app",
            async (HttpContext ctx, NauditDbContext db, IGitHubAppInstallationChecker checker) =>
            {
                var acct = await CurrentAccount.GetAsync(ctx, db);
                if (acct is null) return Results.Unauthorized();

                var logins = await db.GitHubLinks
                    .Where(l => l.AccountId == acct.Id)
                    .Select(l => l.Login)
                    .ToListAsync(ctx.RequestAborted);

                var status = await checker.GetStatusAsync(logins, ctx.RequestAborted);
                return Results.Ok(new
                {
                    installUrl = status.InstallUrl,
                    accounts = status.Accounts.Select(a => new { login = a.Login, installed = a.Installed }),
                });
            }).RequireAuthorization();
    }
}
```

- [ ] **Step 5: Map the endpoint in `Program.cs`**

In `src/Naudit.Web/Program.cs`, inside the `if (uiConfig.Enabled)` block, after `app.MapDataEndpoints();` (and before `app.UseDefaultFiles();`), add:

```csharp
    app.MapGitHubAppEndpoints(
        app.Services.GetRequiredService<GitOptions>(),
        app.Services.GetRequiredService<IOptions<GitHubOptions>>().Value);
```

(`IOptions<GitHubOptions>` resolves to a default `GitHubOptions` even on non-GitHub deployments — the extension method's own guard handles platform/auth gating. `GitOptions`, `IOptions`, and `GitHubOptions` are already imported in `Program.cs`.)

- [ ] **Step 6: Run the endpoint tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubAppEndpointTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Run the full backend suite (guards against wiring regressions)**

Run: `dotnet test Naudit.slnx`
Expected: PASS — whole suite green.

- [ ] **Step 8: Commit**

```bash
git add src/Naudit.Infrastructure/DependencyInjection.cs \
        src/Naudit.Web/Endpoints/GitHubAppEndpoints.cs \
        src/Naudit.Web/Program.cs \
        tests/Naudit.Tests/GitHubAppEndpointTests.cs
git commit -m "feat(webui): GET /api/me/github-app fuer den Installations-Status (nur GitHub+App)"
```

---

### Task 4: Frontend — install banner + status row

Add the API type, the query hook, the reusable banner, and wire it into Dashboard, PendingPage, and Profile.

**Files:**
- Modify: `src/frontend/src/api/types.ts` (add `GitHubAppDto` + `GitHubAppAccount`)
- Modify: `src/frontend/src/hooks/queries.ts` (add `useGitHubApp`)
- Create: `src/frontend/src/components/InstallAppBanner.tsx`
- Modify: `src/frontend/src/components/pages/DashboardPage.tsx` (render the banner at the top)
- Modify: `src/frontend/src/components/pages/PendingPage.tsx` (render the banner)
- Modify: `src/frontend/src/components/pages/ProfilePage.tsx` (status row)

**Interfaces:**
- Consumes: `GET /api/me/github-app` (Task 3) → `{ installUrl, accounts: [{ login, installed }] }`.
- Produces: `useGitHubApp()` hook; `<InstallAppBanner />` component.

- [ ] **Step 1: Add the DTO types**

In `src/frontend/src/api/types.ts`, append:

```ts
export interface GitHubAppAccount {
  login: string;
  installed: boolean | null;
}

export interface GitHubAppDto {
  installUrl: string;
  accounts: GitHubAppAccount[];
}
```

- [ ] **Step 2: Add the query hook**

In `src/frontend/src/hooks/queries.ts`, extend the type import and add the hook. Change the import line:

```ts
import type { AccountsDto, DashboardDto, GitHubAppDto, ReviewDetailDto, SettingsDto, UsageDto } from "@/api/types";
```

and add after `useUsage`:

```ts
/** Installations-Status der GitHub-App. 404 = Feature aus (nicht GitHub+App) ⇒ kein Retry,
 *  data bleibt undefined, das Banner rendert nichts. */
export function useGitHubApp() {
  return useQuery({
    queryKey: ["github-app"],
    queryFn: () => api<GitHubAppDto>("/api/me/github-app"),
    retry: false,
  });
}
```

- [ ] **Step 3: Create the banner component**

Create `src/frontend/src/components/InstallAppBanner.tsx`:

```tsx
import { useGitHubApp } from "@/hooks/queries";

/** Onboarding-Banner: erscheint, solange mindestens ein verknüpfter GitHub-Login die Naudit-App
 *  noch nicht installiert hat. Feature aus / Fehler / alles installiert ⇒ nichts. */
export function InstallAppBanner() {
  const { data } = useGitHubApp();
  if (!data || !data.installUrl) return null;
  const missing = data.accounts.filter((a) => a.installed === false);
  if (missing.length === 0) return null;

  return (
    <div className="flex flex-wrap items-center gap-4 rounded-xl border border-acc/40 bg-acc/10 px-5 py-4">
      <span className="min-w-0 flex-1 text-sm leading-relaxed text-ink">
        Naudit isn’t installed on your GitHub {missing.length === 1 ? "account" : "accounts"} yet
        {" — "}
        <span className="font-mono">{missing.map((a) => a.login).join(", ")}</span>. Install it so
        reviews start running on your repositories.
      </span>
      <a
        href={data.installUrl}
        target="_blank"
        rel="noreferrer"
        className="shrink-0 cursor-pointer rounded-lg bg-acc px-4 py-2 text-sm font-bold text-accink transition-colors hover:bg-acc2 focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-offset-2 focus-visible:outline-teal"
      >
        Install on GitHub
      </a>
    </div>
  );
}
```

- [ ] **Step 4: Render the banner on the Dashboard**

In `src/frontend/src/components/pages/DashboardPage.tsx`:

Add the import near the other component imports (after the `ReviewDetail` import):

```tsx
import { InstallAppBanner } from "@/components/InstallAppBanner";
```

Then, immediately inside the outer `return (`, make the banner the first child of the top-level flex column. Change:

```tsx
    <div className="flex flex-col gap-5 px-7 py-6">
      <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
```

to:

```tsx
    <div className="flex flex-col gap-5 px-7 py-6">
      <InstallAppBanner />
      <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
```

- [ ] **Step 5: Render the banner on the PendingPage**

In `src/frontend/src/components/pages/PendingPage.tsx`:

Add the import after the existing imports:

```tsx
import { InstallAppBanner } from "@/components/InstallAppBanner";
```

Then place the banner below the descriptive paragraph, before the "Sign out" button. Change:

```tsx
        <p className="mt-5 text-sm leading-relaxed text-ink2">
          {rejected
            ? "Your access has been revoked. Contact the administrator if you think this is a mistake."
            : "Your account is waiting for an admin to approve it. Reviews for your repositories start running once you are approved."}
        </p>
        <Button variant="secondary" className="mt-7" onClick={() => void onLogout()}>
          Sign out
        </Button>
```

to:

```tsx
        <p className="mt-5 text-sm leading-relaxed text-ink2">
          {rejected
            ? "Your access has been revoked. Contact the administrator if you think this is a mistake."
            : "Your account is waiting for an admin to approve it. Reviews for your repositories start running once you are approved."}
        </p>
        {!rejected && (
          <div className="mt-6 w-full text-left">
            <InstallAppBanner />
          </div>
        )}
        <Button variant="secondary" className="mt-7" onClick={() => void onLogout()}>
          Sign out
        </Button>
```

- [ ] **Step 6: Add the status row on the ProfilePage**

In `src/frontend/src/components/pages/ProfilePage.tsx`:

Add imports (extend the queries import + add the hook):

```tsx
import { useUsage, useGitHubApp, fmtTokens } from "@/hooks/queries";
```

Add the hook call inside the component, next to `const { data, isLoading } = useUsage();`:

```tsx
  const gitHubApp = useGitHubApp();
```

Then add a status Panel. Insert it right after the closing `</div>` of the header row (the `<Pill kind="ok">✓ active</Pill>` block) and before the `<div className="grid grid-cols-1 items-start gap-4 lg:grid-cols-[2fr_1fr]">` block:

```tsx
      {gitHubApp.data && gitHubApp.data.accounts.length > 0 && (
        <Panel title="GitHub App">
          {gitHubApp.data.accounts.map((a) => (
            <div key={a.login} className="flex items-center gap-4 border-b border-hairline px-5 py-3.5 last:border-b-0">
              <span className="flex-1 truncate font-mono text-[13px]">{a.login}</span>
              {a.installed === true && <Pill kind="ok">✓ installed</Pill>}
              {a.installed === null && <Pill kind="warn">● unknown</Pill>}
              {a.installed === false &&
                (gitHubApp.data!.installUrl ? (
                  <a
                    href={gitHubApp.data!.installUrl}
                    target="_blank"
                    rel="noreferrer"
                    className="cursor-pointer rounded-lg bg-acc px-3 py-1.5 text-xs font-bold text-accink transition-colors hover:bg-acc2"
                  >
                    Install
                  </a>
                ) : (
                  <Pill kind="warn">● not installed</Pill>
                ))}
            </div>
          ))}
        </Panel>
      )}
```

(`Panel` and `Pill` are already imported in `ProfilePage.tsx`. `Pill` accepts `kind` values `"ok" | "warn" | "danger" | "neutral"` — the `"ok"`/`"warn"` used above are valid.)

- [ ] **Step 7: Lint + build the frontend**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: lint clean, `tsc --noEmit` clean, `vite build` succeeds.

- [ ] **Step 8: Commit**

```bash
git add src/frontend/src/api/types.ts \
        src/frontend/src/hooks/queries.ts \
        src/frontend/src/components/InstallAppBanner.tsx \
        src/frontend/src/components/pages/DashboardPage.tsx \
        src/frontend/src/components/pages/PendingPage.tsx \
        src/frontend/src/components/pages/ProfilePage.tsx
git commit -m "feat(webui): Onboarding-Banner zur GitHub-App-Installation (Dashboard, Pending, Profil)"
```

---

### Task 5: Documentation

Document the Setup URL, the install-from-the-UI flow, and that one GitHub App can serve both the bot identity and the WebUI login.

**Files:**
- Modify: `docs/github-app.md`
- Modify: `docs/webui.md`
- Modify: `CLAUDE.md`

**Interfaces:** none (docs only).

- [ ] **Step 1: Extend `docs/github-app.md`**

In the app-creation settings table (the `| Setting | Value |` table under "1. Create the app"), add a row:

```markdown
| **Setup URL** (optional) | `https://<your-host>/` — after "Install app", GitHub sends the user back to the Naudit dashboard; the install banner then clears itself |
```

Then add a new section after "2. Configure Naudit":

```markdown
## Install from the Naudit WebUI

Once `Naudit:GitHub:Auth=App` and the WebUI (`Naudit:Ui:Enabled=true`) are both on, a signed-in
user whose GitHub account/org does not yet have the app installed sees an **install banner** on
the dashboard (and on the pending screen, while they wait for admin approval). The banner links
straight to the app's install page; after installing, GitHub returns them to the Naudit dashboard
(the **Setup URL** above) and the banner disappears — Naudit re-checks the live installation state
on each load (`GET /users/{login}/installation`, org fallback), so it also reflects a later
uninstall. The Profile page shows the same status per linked GitHub login.

This needs no extra configuration beyond `Auth=App`: Naudit derives the install link from the
app's own slug (`GET /app`).

## One app for both bot identity and WebUI login (recommended)

A GitHub App also carries an OAuth **client id/secret**, so the *same* app can power the WebUI
"Sign in with GitHub" — no separate OAuth App needed. In the app settings:

- Note the **Client ID** and generate a **client secret** (app settings → "Client secrets").
- Add the callback URL **`https://<your-host>/auth/callback/github`** (app settings →
  "Callback URL").
- Enable "Request user authorization (OAuth) during installation" **only if** you want the
  install page to double as sign-in; leave it off to keep the Setup-URL return trip described
  above.

Then point the WebUI login at those credentials:

```bash
dotnet user-secrets set "Naudit:Ui:Auth:GitHub:Enabled"      "true"        --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ui:Auth:GitHub:ClientId"     "<app-client-id>"     --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ui:Auth:GitHub:ClientSecret" "<app-client-secret>" --project src/Naudit.Web
```

See [WebUI](webui.md) for the login/approval flow.
```

- [ ] **Step 2: Extend `docs/webui.md`**

Find the GitHub OAuth section (around the "GitHub OAuth (self-service, opt-in)" table / the OAuth App creation block) and add a short note that on `Auth=App` deployments the dashboard/pending page show an install banner. Add after the paragraph that explains GitHub links (the block mentioning `no GitHub link`):

```markdown
On GitHub-App deployments (`Naudit:GitHub:Auth=App`), a signed-in user whose linked GitHub
account/org has not installed the Naudit app yet sees an **install banner** on the dashboard and
the pending screen that links to the app's install page; see
[GitHub App setup](github-app.md#install-from-the-naudit-webui). The banner reflects the live
installation state and clears itself once the app is installed.
```

- [ ] **Step 3: Extend the GitHub App bullet in `CLAUDE.md`**

In `CLAUDE.md`, at the end of the "GitHub App auth" bullet (the paragraph ending "See `docs/github-app.md`."), append:

```markdown
  On WebUI deployments (`Naudit:Ui:Enabled=true`) the App mode also drives an **install-onboarding
  banner**: `GET /api/me/github-app` (`src/Naudit.Web/Endpoints/GitHubAppEndpoints.cs`, mapped only
  when `Platform=GitHub` **and** `Auth=App`) uses `GitHubAppInstallationChecker`
  (`src/Naudit.Infrastructure/Git/GitHub/`, sharing the App-JWT via the extracted `GitHubAppJwt`) to
  live-check `GET /users/{login}/installation` (org fallback) per linked login and derive the install
  deep-link from the app slug (`GET /app`); fail-quiet (API error ⇒ `installed: null`, no banner).
  The SPA renders the banner on the dashboard + pending screen and a status row on the profile.
```

- [ ] **Step 4: Verify docs render / no broken relative links**

Run: `grep -n "install-from-the-naudit-webui" docs/github-app.md docs/webui.md`
Expected: the anchor is defined by the `## Install from the Naudit WebUI` heading in `github-app.md` and referenced from `webui.md` — both appear.

- [ ] **Step 5: Commit**

```bash
git add docs/github-app.md docs/webui.md CLAUDE.md
git commit -m "docs(github-app,webui): Installation aus der WebUI + eine App fuer Bot & Login dokumentieren"
```

---

## Final verification

- [ ] **Backend suite:** `dotnet test Naudit.slnx` → all green.
- [ ] **Frontend:** `cd src/frontend && npm run lint && npm run build` → clean.
- [ ] **Manual smoke (optional, needs a real GitHub App):** run the host with `Naudit:Git:Platform=GitHub`, `Naudit:GitHub:Auth=App`, valid `App:AppId`/`App:PrivateKey`, `Naudit:Ui:Enabled=true`, `Naudit:Db:Enabled=true`; sign in via GitHub with an account that has **not** installed the app → banner appears; install the app → banner clears on reload.
