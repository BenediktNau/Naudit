using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Docker;
using Naudit.Infrastructure.Ui;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class AccountServiceTests
{
    private static NauditDbContext NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"naudit-test-{Guid.NewGuid():N}.db");
        var db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        db.Database.Migrate();
        return db;
    }

    private static AccountService NewService(NauditDbContext db, UiOptions? o = null, SessionContainerManager? sandbox = null) =>
        new(db, o ?? new UiOptions(), sandbox);

    /// <summary>Echter SessionContainerManager als Sandbox-Kollaborateur, mit einem gestellten
    /// IDockerClient (FakeDockerClient oder ThrowingDockerClient) — Pattern aus ClaudeSessionServiceTests.</summary>
    private static SessionContainerManager Sandbox(IDockerClient docker)
        => new(docker, new SessionSandboxOptions(), NullLogger<SessionContainerManager>.Instance, new FakeTime());

    /// <summary>IDockerClient, dessen für RemoveAsync relevante Methoden immer DockerUnavailableException
    /// werfen — simuliert "Docker-Engine down" für den Best-effort-Pfad (Duplikat aus
    /// ClaudeSessionServiceTests: dort private, hier eigenständig gehalten statt geteilt).</summary>
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
    public async Task CreateLocal_isActiveImmediately_andVerifiesPassword()
    {
        await using var db = NewDb();
        var svc = NewService(db);

        var created = await svc.CreateLocalAsync("bene", "sehr-geheim", isAdmin: true, ["BenediktNau"]);

        Assert.Equal(AccountStatus.Active, created.Status);
        Assert.Equal("benediktnau", Assert.Single(created.GitHubLinks).Login); // lowercased
        Assert.NotNull(await svc.VerifyPasswordAsync("bene", "sehr-geheim"));
        Assert.Null(await svc.VerifyPasswordAsync("bene", "falsch"));
    }

    [Fact]
    public async Task CreateLocal_rejectsShortPassword_andDuplicateUsername()
    {
        await using var db = NewDb();
        var svc = NewService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateLocalAsync("a", "kurz", false, []));
        await svc.CreateLocalAsync("dup", "passwort123", false, []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateLocalAsync("dup", "passwort123", false, []));
    }

    [Fact]
    public async Task MaterializeExternal_createsPendingOnce_reusesOnSecondSignIn()
    {
        await using var db = NewDb();
        var svc = NewService(db);

        var first = await svc.MaterializeExternalAsync(AccountProvider.GitHub, "12345", "mmustermann", "mmustermann");
        var second = await svc.MaterializeExternalAsync(AccountProvider.GitHub, "12345", "mmustermann", "mmustermann");

        Assert.Equal(AccountStatus.Pending, first.Status);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.Accounts.CountAsync());
        Assert.Equal("mmustermann", Assert.Single(first.GitHubLinks).Login);
    }

    [Fact]
    public async Task MaterializeExternal_rejectedAccount_returnsToPending_onReSignIn()
    {
        // Ein abgelehnter/entzogener externer User muss sich erneut anmelden können und dabei
        // wieder in Pending landen — sonst sieht der Admin ihn nie wieder (Rejected ist im
        // Dashboard unsichtbar) und kann ihn nicht mehr freigeben.
        await using var db = NewDb();
        var svc = NewService(db);
        var acct = await svc.MaterializeExternalAsync(AccountProvider.GitHub, "12345", "mmustermann", "mmustermann");
        await svc.SetStatusAsync(acct.Id, AccountStatus.Rejected);

        var reSignIn = await svc.MaterializeExternalAsync(AccountProvider.GitHub, "12345", "mmustermann", "mmustermann");

        Assert.Equal(acct.Id, reSignIn.Id);
        Assert.Equal(AccountStatus.Pending, reSignIn.Status);
    }

    [Fact]
    public async Task MaterializeExternal_activeAccount_staysActive_onReSignIn()
    {
        // Gegenprobe: ein bereits freigegebener User darf bei jedem Login NICHT zurückgesetzt werden.
        await using var db = NewDb();
        var svc = NewService(db);
        var acct = await svc.MaterializeExternalAsync(AccountProvider.Oidc, "sub-1", "u", null);
        await svc.SetStatusAsync(acct.Id, AccountStatus.Active);

        var reSignIn = await svc.MaterializeExternalAsync(AccountProvider.Oidc, "sub-1", "u", null);

        Assert.Equal(AccountStatus.Active, reSignIn.Status);
    }

    [Fact]
    public async Task Accounts_providerExternalId_isUnique()
    {
        // DB-Netz gegen doppelte externe Identitäten (paralleler OAuth-Callback): ein zweiter
        // Insert mit gleichem (Provider, ExternalId) muss scheitern.
        await using var db = NewDb();
        db.Accounts.Add(new AccountEntity { Username = "a", Provider = AccountProvider.GitHub, ExternalId = "42", Status = AccountStatus.Pending, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.Accounts.Add(new AccountEntity { Username = "b", Provider = AccountProvider.GitHub, ExternalId = "42", Status = AccountStatus.Pending, CreatedAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Accounts_localWithNullExternalId_doNotCollide()
    {
        // NULL-ExternalId (lokale Accounts) gilt als verschieden — mehrere lokale Accounts sind ok.
        await using var db = NewDb();
        var svc = NewService(db);
        await svc.CreateLocalAsync("u1", "passwort123", false, []);
        await svc.CreateLocalAsync("u2", "passwort123", false, []);
        Assert.Equal(2, await db.Accounts.CountAsync());
    }

    [Fact]
    public async Task MaterializeExternal_adminListGrantsAdmin()
    {
        await using var db = NewDb();
        var svc = NewService(db, new UiOptions { Admins = ["boss"] });
        var acct = await svc.MaterializeExternalAsync(AccountProvider.Oidc, "sub-1", "boss", null);
        Assert.True(acct.IsAdmin);
        Assert.Empty(acct.GitHubLinks); // OIDC ohne GitHub-Login ⇒ kein Link
    }

    [Fact]
    public async Task SetStatus_andSetGitHubLinks_work()
    {
        await using var db = NewDb();
        var svc = NewService(db);
        var acct = await svc.MaterializeExternalAsync(AccountProvider.GitHub, "1", "u", "u");

        Assert.True(await svc.SetStatusAsync(acct.Id, AccountStatus.Active));
        Assert.True(await svc.SetGitHubLinksAsync(acct.Id, ["Acme-Org", "u"]));
        Assert.False(await svc.SetStatusAsync(9999, AccountStatus.Active));

        var links = await db.GitHubLinks.Where(l => l.AccountId == acct.Id).Select(l => l.Login).OrderBy(x => x).ToListAsync();
        Assert.Equal(["acme-org", "u"], links);
    }

    [Fact]
    public async Task SetStatus_awayFromActive_removesSandboxContainerAndVolume()
    {
        // Suspendieren/Deaktivieren (jeder Status ≠ Active) darf das Credential-Volume des
        // Accounts nicht überleben lassen — analog zu ClaudeSessionService bei Token-Löschung.
        await using var db = NewDb();
        var acct = await NewService(db).CreateLocalAsync("bene", "sehr-geheim", isAdmin: false, []);
        var docker = new FakeDockerClient();
        docker.Containers[SessionContainerManager.ContainerName(acct.Id)] = true;
        docker.Volumes.Add(SessionContainerManager.ContainerName(acct.Id));
        var svc = NewService(db, sandbox: Sandbox(docker));

        await svc.SetStatusAsync(acct.Id, AccountStatus.Rejected);

        Assert.Empty(docker.Containers); // stop + rm + rmvol gelaufen
        Assert.Empty(docker.Volumes);
    }

    [Fact]
    public async Task SetStatus_backToActive_doesNotTouchSandbox()
    {
        await using var db = NewDb();
        var acct = await NewService(db).CreateLocalAsync("bene", "sehr-geheim", isAdmin: false, []);
        var docker = new FakeDockerClient();
        docker.Containers[SessionContainerManager.ContainerName(acct.Id)] = true;
        docker.Volumes.Add(SessionContainerManager.ContainerName(acct.Id));
        var svc = NewService(db, sandbox: Sandbox(docker));

        await svc.SetStatusAsync(acct.Id, AccountStatus.Active); // war schon aktiv — Reaktivierung fasst nichts an

        Assert.NotEmpty(docker.Containers);
        Assert.NotEmpty(docker.Volumes);
    }

    [Fact]
    public async Task SetStatus_sandboxFailure_doesNotFailStatusChange()
    {
        await using var db = NewDb();
        var acct = await NewService(db).CreateLocalAsync("bene", "sehr-geheim", isAdmin: false, []);
        var svc = NewService(db, sandbox: Sandbox(new ThrowingDockerClient()));

        var ok = await svc.SetStatusAsync(acct.Id, AccountStatus.Rejected); // darf NICHT werfen — best-effort

        Assert.True(ok);
        var reloaded = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == acct.Id);
        Assert.Equal(AccountStatus.Rejected, reloaded.Status);
    }

    [Fact]
    public async Task Seed_createsAdmin_onlyOnEmptyTable()
    {
        await using var db = NewDb();
        var opts = new UiOptions { Admin = new SeedAdminOptions { Username = "root", InitialPassword = "passwort123" } };
        var svc = NewService(db, opts);

        await svc.SeedAsync();
        await svc.SeedAsync(); // idempotent

        var acct = await db.Accounts.SingleAsync();
        Assert.True(acct.IsAdmin);
        Assert.Equal(AccountStatus.Active, acct.Status);
        Assert.NotNull(await svc.VerifyPasswordAsync("root", "passwort123"));
    }
}
