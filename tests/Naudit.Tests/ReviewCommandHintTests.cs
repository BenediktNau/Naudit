using Naudit.Core.Review;
using Naudit.Infrastructure.Git;
using Xunit;

namespace Naudit.Tests;

public class ReviewCommandHintTests
{
    [Fact]
    public void Inline_whenRenderHintOff_isEmpty()
    {
        var options = new ReviewResolutionOptions { RenderHint = false };

        Assert.Equal(string.Empty, ReviewCommandHint.Inline(options));
        Assert.Equal(string.Empty, ReviewCommandHint.Summary(options));
    }

    [Fact]
    public void Inline_isHiddenHtmlComment()
    {
        var hint = ReviewCommandHint.Inline(new ReviewResolutionOptions());

        Assert.StartsWith("\n\n<!--", hint);
        Assert.EndsWith("-->", hint);
        // Ein "--" im Inneren wuerde den Kommentar aufbrechen und den Rest sichtbar machen.
        var inner = hint[(hint.IndexOf("<!--", StringComparison.Ordinal) + 4)..^3];
        Assert.DoesNotContain("--", inner);
    }

    [Fact]
    public void Inline_whenResolutionDisabled_omitsOkCommand()
    {
        // @naudit ok wird bei ausgeschaltetem Resolution-Tracking still verworfen -> nicht bewerben.
        var hint = ReviewCommandHint.Inline(new ReviewResolutionOptions { Enabled = false });

        Assert.Contains("@naudit fp", hint);
        Assert.DoesNotContain("@naudit ok", hint);
    }

    [Fact]
    public void Summary_whenResolutionDisabled_omitsOkCommand()
    {
        var hint = ReviewCommandHint.Summary(new ReviewResolutionOptions { Enabled = false });

        Assert.Contains("@naudit fp", hint);
        Assert.DoesNotContain("@naudit ok", hint);
        Assert.EndsWith("</details>", hint);
    }

    [Fact]
    public void Summary_isCollapsedDetailsBlock()
    {
        var hint = ReviewCommandHint.Summary(new ReviewResolutionOptions());

        Assert.Contains("<details>", hint);
        Assert.Contains("</details>", hint);
        Assert.Contains("@naudit ok", hint);
    }

    [Fact]
    public void Inline_commandLines_areParsedByFpReplyCommand()
    {
        // Kopplungs-Guard: Hinweistext und Parser duerfen nicht auseinanderlaufen.
        var hint = ReviewCommandHint.Inline(new ReviewResolutionOptions());

        var kinds = hint.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("@naudit", StringComparison.Ordinal))
            .Select(l => FpReplyCommand.TryParse(l))
            .ToList();

        Assert.Equal(2, kinds.Count);
        Assert.All(kinds, k => Assert.NotNull(k));
        Assert.Contains(kinds, k => k!.Kind == ReviewCommandKind.FalsePositive);
        Assert.Contains(kinds, k => k!.Kind == ReviewCommandKind.Accept);
    }
}
