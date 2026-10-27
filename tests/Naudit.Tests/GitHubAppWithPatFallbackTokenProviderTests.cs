using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
using Xunit;

namespace Naudit.Tests;

public class GitHubAppWithPatFallbackTokenProviderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingLogger : ILogger<GitHubAppWithPatFallbackTokenProvider>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
            => Entries.Add((level, fmt(state, ex)));
    }

    // Fake-App-Provider: Repos in "Installed" bekommen ein App-Token, sonst NotInstalled; optional andere Fehler.
    private sealed class FakeAppProvider : IGitTokenProvider
    {
        public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Exception? Failure { get; set; }
        public List<string> Calls { get; } = new();

        public ValueTask<string> ResolveTokenAsync(string projectId, CancellationToken ct = default)
        {
            Calls.Add(projectId);
            if (Failure is not null)
                throw Failure;
            if (!Installed.Contains(projectId))
                throw new GitHubAppNotInstalledException(projectId, "12345");
            return ValueTask.FromResult($"ghs_{projectId}");
        }
    }

    private static GitHubAppWithPatFallbackTokenProvider Provider(
        FakeAppProvider app, string globalToken = "", IEnumerable<ProjectTokenEntry>? projectTokens = null,
        CapturingLogger? log = null, TimeProvider? time = null)
        => new(app, globalToken, projectTokens ?? [], log ?? new CapturingLogger(), time ?? new FakeTime(T0));

    [Fact]
    public async Task ExplicitProjectToken_winsOverApp_withoutCallingApp()
    {
        var app = new FakeAppProvider { Installed = { "octo/repo" } };
        var provider = Provider(app, "global-pat",
            [new ProjectTokenEntry { Project = "octo/repo", Token = "proj-pat" }]);

        Assert.Equal("proj-pat", await provider.ResolveTokenAsync("octo/repo"));
        Assert.Empty(app.Calls);
    }

    [Fact]
    public async Task InstalledApp_usesAppToken()
    {
        var app = new FakeAppProvider { Installed = { "octo/repo" } };
        var provider = Provider(app, "global-pat");

        Assert.Equal("ghs_octo/repo", await provider.ResolveTokenAsync("octo/repo"));
    }

    [Fact]
    public async Task AppNotInstalled_fallsBackToGlobalToken()
    {
        var provider = Provider(new FakeAppProvider(), "global-pat");

        Assert.Equal("global-pat", await provider.ResolveTokenAsync("octo/other"));
    }

    [Fact]
    public async Task AppNotInstalled_withoutGlobalToken_rethrowsNotInstalled()
    {
        // Nur ProjectTokens konfiguriert, Repo nicht gelistet ⇒ es gibt keinen Fallback: alter Fehler.
        var provider = Provider(new FakeAppProvider(), "",
            [new ProjectTokenEntry { Project = "octo/repo", Token = "proj-pat" }]);

        var ex = await Assert.ThrowsAsync<GitHubAppNotInstalledException>(
            () => provider.ResolveTokenAsync("octo/other").AsTask());
        Assert.Contains("octo/other", ex.Message);
    }

    [Fact]
    public async Task OtherAppErrors_propagate_noSilentIdentitySwitch()
    {
        var app = new FakeAppProvider { Failure = new HttpRequestException("503") };
        var provider = Provider(app, "global-pat");

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ResolveTokenAsync("octo/repo").AsTask());
    }

    [Fact]
    public async Task MintFailure_propagates()
    {
        var app = new FakeAppProvider { Failure = new InvalidOperationException("kein Token (404)") };
        var provider = Provider(app, "global-pat");

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ResolveTokenAsync("octo/repo").AsTask());
    }

    [Fact]
    public async Task Fallback_logsWarningOncePerRepo_withoutToken()
    {
        var log = new CapturingLogger();
        var time = new FakeTime(T0);
        var provider = Provider(new FakeAppProvider(), "global-pat", log: log, time: time);

        await provider.ResolveTokenAsync("octo/a");
        time.Now = T0.AddMinutes(10); // Negativ-Cache abgelaufen ⇒ erneuter Lookup, aber keine 2. Warnung
        await provider.ResolveTokenAsync("octo/a");
        await provider.ResolveTokenAsync("octo/b");

        var warnings = log.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("octo/a"));
        Assert.Contains(warnings, w => w.Message.Contains("octo/b"));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("global-pat"));
    }

    [Fact]
    public async Task NotInstalled_isNegativelyCached_untilExpiry_thenAppInstallIsPickedUp()
    {
        var app = new FakeAppProvider();
        var time = new FakeTime(T0);
        var provider = Provider(app, "global-pat", time: time);

        Assert.Equal("global-pat", await provider.ResolveTokenAsync("octo/repo"));
        time.Now = T0.AddMinutes(4);
        Assert.Equal("global-pat", await provider.ResolveTokenAsync("octo/repo"));
        // Innerhalb des Cache-Fensters kein zweiter Installations-Lookup.
        Assert.Single(app.Calls);

        // App wird installiert; nach Ablauf des Negativ-Caches greift wieder die App.
        app.Installed.Add("octo/repo");
        time.Now = T0.AddMinutes(6);
        Assert.Equal("ghs_octo/repo", await provider.ResolveTokenAsync("octo/repo"));
        Assert.Equal(2, app.Calls.Count);
    }

    [Theory]
    [InlineData(GitHubAuthKind.App, "pat", false, true)]
    [InlineData(GitHubAuthKind.App, "", true, true)]
    [InlineData(GitHubAuthKind.App, "", false, false)]
    [InlineData(GitHubAuthKind.App, "   ", false, false)]
    [InlineData(GitHubAuthKind.Pat, "pat", true, false)]
    public void UsesAppWithPatFallback_onlyForAppWithPatConfig(GitHubAuthKind auth, string token, bool projectToken, bool expected)
    {
        var options = new GitHubOptions { Auth = auth, Token = token };
        if (projectToken)
            options.ProjectTokens.Add(new ProjectTokenEntry { Project = "octo/repo", Token = "proj-pat" });

        Assert.Equal(expected, options.UsesAppWithPatFallback());
    }

    [Theory]
    [InlineData(false, false)] // fester InstallationId + nur globaler PAT ⇒ kein Fallback
    [InlineData(true, true)]   // Per-Projekt-Overrides wirken weiter
    public void UsesAppWithPatFallback_fixedInstallationId_ignoresGlobalToken(bool projectToken, bool expected)
    {
        var options = new GitHubOptions { Auth = GitHubAuthKind.App, Token = "pat" };
        options.App.InstallationId = 42;
        if (projectToken)
            options.ProjectTokens.Add(new ProjectTokenEntry { Project = "octo/repo", Token = "proj-pat" });

        Assert.Equal(expected, options.UsesAppWithPatFallback());
        Assert.True(options.GlobalPatFallbackIgnored());
    }

    [Theory]
    [InlineData(GitHubAuthKind.App, "pat", null, false)]
    [InlineData(GitHubAuthKind.App, "", 42L, false)]
    [InlineData(GitHubAuthKind.Pat, "pat", 42L, false)]
    [InlineData(GitHubAuthKind.App, "pat", 42L, true)]
    public void GlobalPatFallbackIgnored_onlyForAppWithFixedInstallationAndToken(
        GitHubAuthKind auth, string token, long? installationId, bool expected)
    {
        var options = new GitHubOptions { Auth = auth, Token = token };
        options.App.InstallationId = installationId;
        Assert.Equal(expected, options.GlobalPatFallbackIgnored());
    }

    [Fact]
    public void UsesAppWithPatFallback_ignoresBlankProjectTokenEntries()
    {
        var options = new GitHubOptions { Auth = GitHubAuthKind.App };
        options.ProjectTokens.Add(new ProjectTokenEntry { Project = "octo/repo", Token = " " });

        Assert.False(options.UsesAppWithPatFallback());
    }
}
