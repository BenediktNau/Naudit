using Microsoft.Extensions.Configuration;
using Naudit.Core.Review;
using Xunit;

namespace Naudit.Tests;

public class ReviewContextOptionsTests
{
    [Fact]
    public void Defaults_areOn_withModerateBudget()
    {
        var ctx = new ReviewOptions().Context;

        Assert.True(ctx.Enabled);
        Assert.Equal(40_000, ctx.MaxChars);
        Assert.Equal(400, ctx.FullFileMaxLines);
        Assert.Equal(30, ctx.BlockPadLines);
        Assert.Equal(3, ctx.UsageSnippetLines);
        Assert.Equal(5, ctx.MaxUsagesPerSymbol);
        Assert.Equal(3, ctx.MaxTreeDepth);
        Assert.Equal(50, ctx.ReadmeMaxLines);
    }

    [Fact]
    public void Context_bindsFromConfiguration_underNauditReview()
    {
        // Genau der Bindungs-Pfad aus AddNauditInfrastructure: GetSection("Naudit:Review").Get<ReviewOptions>().
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Naudit:Review:Context:Enabled"] = "false",
                ["Naudit:Review:Context:MaxChars"] = "12345",
                ["Naudit:Review:Context:FullFileMaxLines"] = "200",
            })
            .Build();

        var options = config.GetSection("Naudit:Review").Get<ReviewOptions>()!;

        Assert.False(options.Context.Enabled);
        Assert.Equal(12345, options.Context.MaxChars);
        Assert.Equal(200, options.Context.FullFileMaxLines);
    }

    [Fact]
    public void ReviewContext_Empty_isEmpty()
    {
        var empty = Naudit.Core.Models.ReviewContext.Empty;

        Assert.Empty(empty.Environments);
        Assert.Empty(empty.Usages);
        Assert.Null(empty.Overview);
    }
}
