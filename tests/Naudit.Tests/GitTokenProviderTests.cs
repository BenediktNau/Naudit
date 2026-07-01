using Naudit.Infrastructure.Git;
using Xunit;

namespace Naudit.Tests;

public class GitTokenProviderTests
{
    private static ConfiguredGitTokenProvider Provider(string @default, params (string Project, string Token)[] entries)
        => new(@default, entries.Select(e => new ProjectTokenEntry { Project = e.Project, Token = e.Token }));

    [Fact]
    public void ResolveToken_projectOverride_wins()
    {
        var p = Provider("default-tok", ("octo/repo", "proj-tok"));
        Assert.Equal("proj-tok", p.ResolveToken("octo/repo"));
    }

    [Fact]
    public void ResolveToken_unknownProject_fallsBackToDefault()
    {
        var p = Provider("default-tok");
        Assert.Equal("default-tok", p.ResolveToken("octo/other"));
    }

    [Fact]
    public void ResolveToken_blankOverride_fallsBackToDefault()
    {
        // Leerer Token-Wert wird im Ctor verworfen ⇒ kein leerer Auth-Header, sondern Default.
        var p = Provider("default-tok", ("octo/repo", "   "));
        Assert.Equal("default-tok", p.ResolveToken("octo/repo"));
    }

    [Fact]
    public void ResolveToken_isCaseInsensitiveForOwnerRepoKeys()
    {
        var p = Provider("default-tok", ("Octo/Repo", "proj-tok"));
        Assert.Equal("proj-tok", p.ResolveToken("octo/repo"));
    }

    [Fact]
    public void ResolveToken_lastEntryWins_onDuplicateProject()
    {
        var p = Provider("default-tok", ("octo/repo", "first"), ("octo/repo", "second"));
        Assert.Equal("second", p.ResolveToken("octo/repo"));
    }
}
