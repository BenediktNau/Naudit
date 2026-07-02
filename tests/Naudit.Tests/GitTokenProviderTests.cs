using Naudit.Infrastructure.Git;
using Xunit;

namespace Naudit.Tests;

public class GitTokenProviderTests
{
    private static ConfiguredGitTokenProvider Provider(string @default, params (string Project, string Token)[] entries)
        => new(@default, entries.Select(e => new ProjectTokenEntry { Project = e.Project, Token = e.Token }));

    [Fact]
    public async Task ResolveToken_projectOverride_wins()
    {
        var p = Provider("default-tok", ("octo/repo", "proj-tok"));
        Assert.Equal("proj-tok", await p.ResolveTokenAsync("octo/repo"));
    }

    [Fact]
    public async Task ResolveToken_unknownProject_fallsBackToDefault()
    {
        var p = Provider("default-tok");
        Assert.Equal("default-tok", await p.ResolveTokenAsync("octo/other"));
    }

    [Fact]
    public async Task ResolveToken_blankOverride_fallsBackToDefault()
    {
        // Leerer Token-Wert wird im Ctor verworfen ⇒ kein leerer Auth-Header, sondern Default.
        var p = Provider("default-tok", ("octo/repo", "   "));
        Assert.Equal("default-tok", await p.ResolveTokenAsync("octo/repo"));
    }

    [Fact]
    public async Task ResolveToken_isCaseInsensitiveForOwnerRepoKeys()
    {
        var p = Provider("default-tok", ("Octo/Repo", "proj-tok"));
        Assert.Equal("proj-tok", await p.ResolveTokenAsync("octo/repo"));
    }

    [Fact]
    public async Task ResolveToken_lastEntryWins_onDuplicateProject()
    {
        var p = Provider("default-tok", ("octo/repo", "first"), ("octo/repo", "second"));
        Assert.Equal("second", await p.ResolveTokenAsync("octo/repo"));
    }
}
