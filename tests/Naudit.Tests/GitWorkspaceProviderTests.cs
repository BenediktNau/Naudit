using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Sast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitWorkspaceProviderTests
{
    private static readonly ReviewRequest Request = new("1", 42, "T");

    [Fact]
    public async Task CheckoutAsync_runsInitFetchCheckout_andExposesRoot()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, "", ""));
        var git = new FakeGitPlatform([]); // GetCheckoutAsync liefert Dummy-URL/Ref
        var provider = new GitWorkspaceProvider(git, runner, NullLogger<GitWorkspaceProvider>.Instance);

        await using var ws = await provider.CheckoutAsync(Request);

        Assert.True(Directory.Exists(ws.RootPath));
        var gitArgs = runner.Specs.Select(s => string.Join(" ", s.Arguments)).ToList();
        Assert.Contains(gitArgs, a => a.StartsWith("init"));
        Assert.Contains(gitArgs, a => a.Contains("remote") && a.Contains("add") && a.Contains("origin"));
        Assert.Contains(gitArgs, a => a.Contains("fetch") && a.Contains("refs/test/head"));
        Assert.Contains(gitArgs, a => a.Contains("checkout") && a.Contains("FETCH_HEAD"));
        Assert.All(runner.Specs, s => Assert.Equal("git", s.FileName));
    }

    [Fact]
    public async Task CheckoutAsync_passesAuthOnlyViaFetchEnvironment_neverInArguments()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, "", ""));
        var provider = new GitWorkspaceProvider(new FakeGitPlatform([]), runner, NullLogger<GitWorkspaceProvider>.Instance);

        await using var ws = await provider.CheckoutAsync(Request);

        // Header weder in argv (ps-sichtbar) noch per `remote add` in .git/config.
        Assert.All(runner.Specs, s => Assert.DoesNotContain(s.Arguments, a => a.Contains("dG9rZW4tc2VjcmV0")));
        var remote = runner.Specs.Single(s => s.Arguments.Contains("remote"));
        Assert.Contains("https://host/repo.git", remote.Arguments);

        var fetch = runner.Specs.Single(s => s.Arguments.Contains("fetch"));
        Assert.NotNull(fetch.Environment);
        Assert.Equal("1", fetch.Environment!["GIT_CONFIG_COUNT"]);
        Assert.Equal("http.extraHeader", fetch.Environment["GIT_CONFIG_KEY_0"]);
        Assert.Equal("Authorization: Basic dG9rZW4tc2VjcmV0", fetch.Environment["GIT_CONFIG_VALUE_0"]);

        // Nur der Fetch braucht das Token.
        Assert.All(runner.Specs.Where(s => s != fetch),
            s => Assert.False(s.Environment?.ContainsKey("GIT_CONFIG_VALUE_0") ?? false));
    }

    [Fact]
    public async Task CheckoutAsync_runsGitWithMinimalEnvironment_andWithoutSymlinks()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, "", ""));
        var provider = new GitWorkspaceProvider(new FakeGitPlatform([]), runner, NullLogger<GitWorkspaceProvider>.Instance);

        await using var ws = await provider.CheckoutAsync(Request);

        // Kein git-Lauf erbt die volle Host-Env (Naudit-Secrets).
        Assert.All(runner.Specs, s => Assert.NotNull(s.InheritedEnvironment));
        Assert.All(runner.Specs, s => Assert.DoesNotContain(s.InheritedEnvironment!, e => e.StartsWith("Naudit", StringComparison.OrdinalIgnoreCase)));

        // Symlinks aus dem PR werden als reine Textdateien ausgecheckt.
        var checkout = runner.Specs.Single(s => s.Arguments.Contains("checkout"));
        var args = string.Join(" ", checkout.Arguments);
        Assert.Contains("-c core.symlinks=false", args);
    }

    [Fact]
    public async Task CheckoutAsync_scrubsAuthHeaderFromLoggedStderr()
    {
        var runner = new StubProcessRunner(s =>
            s.Arguments.Contains("fetch")
                ? new ProcessResult(128, "", "fatal: header 'Authorization: Basic dG9rZW4tc2VjcmV0' rejected")
                : new ProcessResult(0, "", ""));
        var logger = new CapturingLogger<GitWorkspaceProvider>();
        var provider = new GitWorkspaceProvider(new FakeGitPlatform([]), runner, logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CheckoutAsync(Request));

        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, m => Assert.DoesNotContain("dG9rZW4tc2VjcmV0", m));
    }

    [Fact]
    public async Task CheckoutAsync_realGit_checksOutSymlinkAsPlainFile()
    {
        // Integration: echter git-Lauf gegen ein lokales Bare-Repo mit Symlink auf /etc/passwd.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var real = new SystemProcessRunner();
        var src = Directory.CreateTempSubdirectory("naudit-src-").FullName;
        try
        {
            async Task Git(params string[] a)
            {
                var r = await real.RunAsync(new ProcessSpec("git", a, null,
                    new Dictionary<string, string?>
                    {
                        ["GIT_AUTHOR_NAME"] = "t", ["GIT_AUTHOR_EMAIL"] = "t@t", ["GIT_COMMITTER_NAME"] = "t", ["GIT_COMMITTER_EMAIL"] = "t@t",
                    }, src, TimeSpan.FromSeconds(30)));
                Assert.True(r.ExitCode == 0, r.StdErr);
            }
            await Git("init", "-q");
            File.CreateSymbolicLink(Path.Combine(src, "README.md"), "/etc/passwd");
            await Git("add", "-A");
            await Git("-c", "commit.gpgsign=false", "commit", "-q", "-m", "x");
            await Git("update-ref", "refs/test/head", "HEAD");

            var platform = new FakeGitPlatform([]) { CheckoutInfo = new Naudit.Core.Abstractions.RepoCheckoutInfo(src, "refs/test/head") };
            var provider = new GitWorkspaceProvider(platform, real, NullLogger<GitWorkspaceProvider>.Instance);

            await using var ws = await provider.CheckoutAsync(Request);

            var readme = new FileInfo(Path.Combine(ws.RootPath, "README.md"));
            Assert.True(readme.Exists);
            Assert.Null(readme.LinkTarget);                                   // keine Verknüpfung
            Assert.Equal("/etc/passwd", File.ReadAllText(readme.FullName));  // nur der Linktext
        }
        finally { Directory.Delete(src, recursive: true); }
    }

    [Fact]
    public async Task DisposeAsync_deletesWorkspace()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, "", ""));
        var provider = new GitWorkspaceProvider(new FakeGitPlatform([]), runner, NullLogger<GitWorkspaceProvider>.Instance);

        var ws = await provider.CheckoutAsync(Request);
        var path = ws.RootPath;
        await ws.DisposeAsync();

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task CheckoutAsync_throwsAndCleansUp_whenGitFails()
    {
        var runner = new StubProcessRunner(s =>
            string.Join(" ", s.Arguments).Contains("fetch")
                ? new ProcessResult(128, "", "fatal: couldn't fetch")
                : new ProcessResult(0, "", ""));
        var provider = new GitWorkspaceProvider(new FakeGitPlatform([]), runner, NullLogger<GitWorkspaceProvider>.Instance);

        var before = Directory.GetDirectories(Path.GetTempPath(), "naudit-*").ToHashSet();
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CheckoutAsync(Request));
        var after = Directory.GetDirectories(Path.GetTempPath(), "naudit-*");
        Assert.Empty(after.Except(before)); // kein naudit-* Verzeichnis hat den Fehler überlebt
    }
}
