using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Infrastructure.Data;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class SandboxSweeperServiceTests
{
    private static (SandboxSweeperService Sweeper, FakeDockerClient Docker, SessionContainerManager Manager, SessionSandboxState State, FakeTime Time)
        Create(FakeDockerClient? docker = null, SessionSandboxOptions? options = null,
            IReadOnlyList<AccountEntity>? accounts = null)
    {
        docker ??= new FakeDockerClient();
        var time = new FakeTime();
        var manager = new SessionContainerManager(docker, options ?? new SessionSandboxOptions(),
            NullLogger<SessionContainerManager>.Instance, time);
        var state = new SessionSandboxState();
        var sweeper = new SandboxSweeperService(docker, manager, state, Scopes(accounts),
            NullLogger<SandboxSweeperService>.Instance);
        return (sweeper, docker, manager, state, time);
    }

    /// <summary>Scope-Factory mit echtem NauditDbContext auf einer temporären SQLite-Datei — die
    /// Reconciliation fragt die Accounts pro Tick in einem eigenen Scope ab (Sweeper ist Singleton).</summary>
    private static IServiceScopeFactory Scopes(IReadOnlyList<AccountEntity>? accounts)
    {
        var path = Path.Combine(Path.GetTempPath(), $"naudit-sweeper-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDbContext<NauditDbContext>(o => o.UseSqlite($"Data Source={path}"));
        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
            db.Database.Migrate();
            if (accounts is { Count: > 0 })
            {
                db.Accounts.AddRange(accounts);
                db.SaveChanges();
            }
        }
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    [Fact]
    public async Task Adopt_pingFails_setsStateFalse_andSkipsAdoption()
    {
        var docker = new FakeDockerClient { PingResult = false, Containers = { ["naudit-session-1"] = true } };
        var (sweeper, _, manager, state, _) = Create(docker);

        await sweeper.AdoptAsync(CancellationToken.None);

        Assert.False(state.SocketReachable);
        Assert.Null(manager.LastUsed(1)); // keine Adoption ohne erreichbaren Socket
    }

    [Fact]
    public async Task Adopt_pingOk_adoptsExistingContainers()
    {
        var docker = new FakeDockerClient { Containers = { ["naudit-session-8"] = true } };
        var (sweeper, _, manager, state, _) = Create(docker);

        await sweeper.AdoptAsync(CancellationToken.None);

        Assert.True(state.SocketReachable);
        Assert.NotNull(manager.LastUsed(8));
    }

    [Fact]
    public async Task Tick_sweepsIdleContainers()
    {
        var (sweeper, docker, manager, _, time) = Create(
            options: new SessionSandboxOptions { IdleTimeout = TimeSpan.FromHours(1) });
        await manager.EnsureRunningAsync(1);
        time.UtcNow = time.UtcNow.AddHours(2);

        await sweeper.TickAsync(CancellationToken.None);

        Assert.Contains("stop:naudit-session-1", docker.Calls);
    }

    [Fact]
    public async Task Tick_pingRecovers_updatesState_beforeSweeping()
    {
        var (sweeper, docker, _, state, _) = Create();
        state.ReportPing(false);
        docker.PingResult = true;

        await sweeper.TickAsync(CancellationToken.None);

        Assert.True(state.SocketReachable); // Selbstheilung: Runner nutzt Docker wieder
    }

    [Fact]
    public async Task Tick_pingDown_setsStateFalse_andSkipsSweep()
    {
        var docker = new FakeDockerClient { PingResult = false, Containers = { ["naudit-session-1"] = true } };
        var (sweeper, _, _, state, _) = Create(docker);

        await sweeper.TickAsync(CancellationToken.None);

        Assert.False(state.SocketReachable);
        Assert.DoesNotContain(docker.Calls, c => c.StartsWith("stop:"));
    }

    /// <summary>Reconciliation: Container samt Credential-Volume dürfen einen Entzug der Berechtigung
    /// nicht überleben. Der Sofort-Abbau in ClaudeSessionService/AccountService ist best-effort
    /// (Docker-Fehler, laufender Exec, Absturz mitten im Löschen) — dies ist das Netz darunter.</summary>
    [Fact]
    public async Task Tick_reconcile_removesContainersOfAccountsWithoutValidAuthorization()
    {
        var accounts = new[]
        {
            Account(2, AccountStatus.Rejected, token: "enc"),    // Berechtigung entzogen
            Account(3, AccountStatus.Active, token: null),       // Token gelöscht ⇒ keine Zustimmung mehr
        };
        var docker = new FakeDockerClient
        {
            Containers = { ["naudit-session-2"] = true, ["naudit-session-3"] = false, ["naudit-session-4"] = true },
        };
        docker.Volumes.UnionWith(["naudit-session-2", "naudit-session-3", "naudit-session-4"]);
        var (sweeper, _, _, _, _) = Create(docker, accounts: accounts); // Konto 4 existiert gar nicht (mehr)

        await sweeper.TickAsync(CancellationToken.None);

        Assert.Empty(docker.Containers);
        Assert.Empty(docker.Volumes);
    }

    [Fact]
    public async Task Tick_reconcile_keepsContainerOfActiveAccountWithToken()
    {
        var docker = new FakeDockerClient { Containers = { ["naudit-session-5"] = true } };
        docker.Volumes.Add("naudit-session-5");
        var (sweeper, _, manager, _, _) = Create(docker,
            accounts: [Account(5, AccountStatus.Active, token: "enc")]);
        manager.Touch(5); // frisch genutzt — sonst räumt ihn schon der Idle-Sweep ab (anderer Pfad)

        await sweeper.TickAsync(CancellationToken.None);

        Assert.True(docker.Containers["naudit-session-5"]);
        Assert.Contains("naudit-session-5", docker.Volumes);
    }

    private static AccountEntity Account(int id, AccountStatus status, string? token) => new()
    {
        Id = id,
        Username = $"user{id}",
        Provider = AccountProvider.Local,
        Status = status,
        ClaudeSessionToken = token,
    };
}
