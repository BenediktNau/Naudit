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
