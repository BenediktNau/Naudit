using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Docker;
using Naudit.Infrastructure.Ui;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ClaudeSessionServiceTests
{
    private static AccountEntity Account(TestDb db, string username = "alice",
        AccountProvider provider = AccountProvider.GitHub, AccountStatus status = AccountStatus.Active)
    {
        var a = new AccountEntity { Username = username, Provider = provider, Status = status, CreatedAt = DateTime.UtcNow };
        db.Context.Accounts.Add(a);
        db.Context.SaveChanges();
        return a;
    }

    private static ClaudeSessionService Service(TestDb db) =>
        new(db.Context, new EphemeralDataProtectionProvider());

    /// <summary>Echter SessionContainerManager als Sandbox-Kollaborateur, mit einem gestellten
    /// IDockerClient (FakeDockerClient oder ThrowingDockerClient).</summary>
    private static SessionContainerManager Sandbox(IDockerClient docker)
        => new(docker, new SessionSandboxOptions(), NullLogger<SessionContainerManager>.Instance, new FakeTime());

    /// <summary>IDockerClient, dessen für RemoveAsync relevante Methoden immer DockerUnavailableException
    /// werfen — simuliert "Docker-Engine down" für den Best-effort-Pfad.</summary>
    private sealed class ThrowingDockerClient : IDockerClient
    {
        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> InspectSelfImageAsync(string hostname, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<ContainerInfo?> InspectContainerAsync(string name, CancellationToken ct = default) => Task.FromResult<ContainerInfo?>(null);
        public Task RunDetachedAsync(ContainerRunSpec spec, CancellationToken ct = default) => Task.CompletedTask;
        public Task StartAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(string name, CancellationToken ct = default) => throw new DockerUnavailableException("fake: docker down");
        public Task RemoveContainerAsync(string name, CancellationToken ct = default) => throw new DockerUnavailableException("fake: docker down");
        public Task RemoveVolumeAsync(string name, CancellationToken ct = default) => throw new DockerUnavailableException("fake: docker down");
        public Task WriteFileAsync(string name, string directory, string fileName, string content, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DockerExecResult> ExecAsync(string name, IReadOnlyList<string> argv,
            IReadOnlyDictionary<string, string?>? environment, string workingDirectory, CancellationToken ct = default)
            => Task.FromResult(new DockerExecResult(0, "", ""));
        public Task<IReadOnlyList<ContainerListEntry>> ListContainersAsync(string namePrefix, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ContainerListEntry>>(new List<ContainerListEntry>());
        public Task CreateNetworkAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveNetworkAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListNetworksAsync(string namePrefix, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(new List<string>());
    }

    [Fact]
    public async Task SetToken_encryptsAtRest_andRoundTrips()
    {
        using var db = new TestDb();
        var acct = Account(db);
        var svc = Service(db);

        await svc.SetTokenAsync(acct.Id, "sk-ant-oat01-geheim", null);

        Assert.NotNull(acct.ClaudeSessionToken);
        Assert.DoesNotContain("geheim", acct.ClaudeSessionToken);      // verschlüsselt at rest
        Assert.NotNull(acct.ClaudeSessionUpdatedAtUtc);
        Assert.Equal("sk-ant-oat01-geheim", svc.DecryptToken(acct));   // Roundtrip
    }

    [Fact]
    public async Task SetToken_gitHubAccount_autoFillsGitAuthorLogin_lowercased()
    {
        using var db = new TestDb();
        var acct = Account(db, username: "Alice", provider: AccountProvider.GitHub);

        await Service(db).SetTokenAsync(acct.Id, "tok", null);

        Assert.Equal("alice", acct.GitAuthorLogin);
    }

    [Fact]
    public async Task SetToken_explicitLogin_winsOverAutoFill()
    {
        using var db = new TestDb();
        var acct = Account(db);

        await Service(db).SetTokenAsync(acct.Id, "tok", "Bob-GitLab");

        Assert.Equal("bob-gitlab", acct.GitAuthorLogin);
    }

    [Fact]
    public async Task FindByAuthorLogin_matchesCaseInsensitive_onlyActiveWithToken()
    {
        using var db = new TestDb();
        var active = Account(db, "alice");
        var pending = Account(db, "bob", status: AccountStatus.Pending);
        var svc = Service(db);
        await svc.SetTokenAsync(active.Id, "tok", "alice");
        await svc.SetTokenAsync(pending.Id, "tok", "bob");

        Assert.Equal(active.Id, (await svc.FindByAuthorLoginAsync("ALICE"))!.Id);
        Assert.Null(await svc.FindByAuthorLoginAsync("bob"));       // pending zählt nicht
        Assert.Null(await svc.FindByAuthorLoginAsync("unbekannt"));
    }

    [Fact]
    public async Task RemoveToken_clearsTokenAndTimestamp_keepsLogin()
    {
        using var db = new TestDb();
        var acct = Account(db);
        var svc = Service(db);
        await svc.SetTokenAsync(acct.Id, "tok", "alice");

        await svc.RemoveTokenAsync(acct.Id);

        Assert.Null(acct.ClaudeSessionToken);
        Assert.Null(acct.ClaudeSessionUpdatedAtUtc);
        Assert.Equal("alice", acct.GitAuthorLogin);   // Login bleibt — Nutzer will nur den Token weg
    }

    [Fact]
    public async Task RemoveToken_removesSandboxContainerAndVolume()
    {
        using var db = new TestDb();
        var acct = Account(db);
        var docker = new FakeDockerClient();
        docker.Containers[SessionContainerManager.ContainerName(acct.Id)] = true;
        docker.Volumes.Add(SessionContainerManager.ContainerName(acct.Id));
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider(), Sandbox(docker));

        await svc.RemoveTokenAsync(acct.Id);

        Assert.Empty(docker.Containers); // stop + rm + rmvol gelaufen
        Assert.Empty(docker.Volumes);
    }

    [Fact]
    public async Task SetShareInPool_false_removesContainer_true_doesNot()
    {
        using var db = new TestDb();
        var acct = Account(db);
        var docker = new FakeDockerClient();
        docker.Containers[SessionContainerManager.ContainerName(acct.Id)] = true;
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider(), Sandbox(docker));

        await svc.SetShareInPoolAsync(acct.Id, share: true);
        Assert.NotEmpty(docker.Containers); // Opt-in räumt nichts ab

        await svc.SetShareInPoolAsync(acct.Id, share: false);
        Assert.Empty(docker.Containers);
    }

    [Fact]
    public async Task RemoveToken_sandboxFailure_doesNotFailTokenRemoval()
    {
        using var db = new TestDb();
        var acct = Account(db);
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider(), Sandbox(new ThrowingDockerClient()));
        await svc.SetTokenAsync(acct.Id, "tok", "alice");

        await svc.RemoveTokenAsync(acct.Id); // darf NICHT werfen — best-effort

        Assert.Null(acct.ClaudeSessionToken);
        Assert.Null(acct.ClaudeSessionUpdatedAtUtc);
    }

    [Fact]
    public async Task RemoveToken_withoutSandbox_isUnchanged()
    {
        using var db = new TestDb();
        var acct = Account(db);
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider()); // sandbox = null (None-Modus)
        await svc.SetTokenAsync(acct.Id, "tok", "alice");

        await svc.RemoveTokenAsync(acct.Id); // wie bisher, kein Docker-Kontakt

        Assert.Null(acct.ClaudeSessionToken);
    }

    [Fact]
    public void DecryptToken_undecryptable_returnsNull()
    {
        using var db = new TestDb();
        var acct = Account(db);
        acct.ClaudeSessionToken = "CfDJ8-kaputt-nicht-entschluesselbar";
        db.Context.SaveChanges();

        // Keyring weg / fremder Ciphertext ⇒ null statt Crash (Semantik wie DbSettingsLoader).
        Assert.Null(Service(db).DecryptToken(acct));
    }

    [Fact]
    public async Task GetPoolCandidates_onlyActiveWithTokenAndOptIn_idSorted()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());

        // aktiv + Token + Opt-in ⇒ drin
        var inPool = new AccountEntity { Username = "a", Provider = AccountProvider.GitHub, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow, ShareSessionInPool = true };
        // Token, aber KEIN Opt-in ⇒ draußen
        var noOptIn = new AccountEntity { Username = "b", Provider = AccountProvider.GitHub, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow, ShareSessionInPool = false };
        // Opt-in, aber KEIN Token ⇒ draußen
        var noToken = new AccountEntity { Username = "c", Provider = AccountProvider.GitHub, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow, ShareSessionInPool = true };
        // Opt-in + Token, aber pending ⇒ draußen
        var pending = new AccountEntity { Username = "d", Provider = AccountProvider.GitHub, Status = AccountStatus.Pending, CreatedAt = DateTime.UtcNow, ShareSessionInPool = true };
        db.Context.Accounts.AddRange(inPool, noOptIn, noToken, pending);
        await db.Context.SaveChangesAsync();
        await svc.SetTokenAsync(inPool.Id, "t", "a");
        await svc.SetTokenAsync(noOptIn.Id, "t", "b");
        await svc.SetTokenAsync(pending.Id, "t", "d");

        var pool = await svc.GetPoolCandidatesAsync();

        Assert.Equal(new[] { inPool.Id }, pool.Select(a => a.Id).ToArray());
    }

    [Fact]
    public async Task SetShareInPool_togglesFlag()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var a = new AccountEntity { Username = "a", Provider = AccountProvider.Local, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow };
        db.Context.Accounts.Add(a);
        await db.Context.SaveChangesAsync();

        await svc.SetShareInPoolAsync(a.Id, true);
        Assert.True((await db.Context.Accounts.FindAsync(a.Id))!.ShareSessionInPool);
        await svc.SetShareInPoolAsync(a.Id, false);
        db.Context.ChangeTracker.Clear();
        Assert.False((await db.Context.Accounts.AsNoTracking().SingleAsync(x => x.Id == a.Id)).ShareSessionInPool);
    }

    [Fact]
    public void DecryptToken_returnsNull_onMalformedCiphertext()
    {
        using var db = new TestDb();
        var acct = Account(db);
        acct.ClaudeSessionToken = "not-valid-base64url!!"; // kaputtes Base64url ⇒ FormatException
        db.Context.SaveChanges();

        Assert.Null(Service(db).DecryptToken(acct));
    }

    [Fact]
    public async Task DeletingAccount_nullsReviewAttribution_keepsReview()
    {
        using var db = new TestDb();
        var acct = Account(db);
        var project = new ProjectEntity
        {
            PlatformProjectId = "owner/repo",
            FirstReviewedAt = DateTime.UtcNow,
            LastReviewedAt = DateTime.UtcNow,
        };
        var review = new ReviewEntity
        {
            Project = project, PrNumber = 1, Title = "T", Verdict = "approve", Summary = "S",
            CreatedAt = DateTime.UtcNow, AiSessionAccountId = acct.Id,
        };
        db.Context.Reviews.Add(review);
        await db.Context.SaveChangesAsync();
        var reviewId = review.Id;

        // ChangeTracker leeren: Account und Review sind danach untracked. Der Fix-up beim
        // Löschen muss also wirklich über die DB (FK-Constraint "ON DELETE SET NULL",
        // SQLite `PRAGMA foreign_keys`) laufen — nicht bloß über EFs In-Memory-Graph.
        db.Context.ChangeTracker.Clear();
        var reloadedAcct = await db.Context.Accounts.SingleAsync(a => a.Id == acct.Id);
        db.Context.Accounts.Remove(reloadedAcct);
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();

        var reloadedReview = await db.Context.Reviews.AsNoTracking().SingleAsync(r => r.Id == reviewId);
        Assert.Null(reloadedReview.AiSessionAccountId);   // Zuordnung weg …
        Assert.Empty(await db.Context.Accounts.ToListAsync());   // … Account wirklich gelöscht, Review bleibt (kein Cascade-Delete).
    }
}
