// tests/Naudit.Tests/FpReplyCommandTests.cs
using Naudit.Infrastructure.Git;
using Xunit;

namespace Naudit.Tests;

public class FpReplyCommandTests
{
    [Theory]
    [InlineData("@naudit fp")]
    [InlineData("  @naudit   fp  ")]
    [InlineData("@Naudit FP")]
    [InlineData("@naudit false-positive")]
    public void TryParse_recognisesCommand_withoutReason(string body)
    {
        var cmd = FpReplyCommand.TryParse(body);
        Assert.NotNull(cmd);
        Assert.Null(cmd!.Reason);
    }

    [Theory]
    [InlineData("@naudit fp this is intentional", "this is intentional")]
    [InlineData("@naudit false-positive because legacy", "because legacy")]
    [InlineData("@NAUDIT FP  trailing spaces  ", "trailing spaces")]
    public void TryParse_extractsReason_restOfLine(string body, string expected)
    {
        var cmd = FpReplyCommand.TryParse(body);
        Assert.NotNull(cmd);
        Assert.Equal(expected, cmd!.Reason);
    }

    [Fact]
    public void TryParse_readsOnlyFirstLine_forReason()
    {
        var cmd = FpReplyCommand.TryParse("@naudit fp only this\nnot this");
        Assert.Equal("only this", cmd!.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("looks good to me")]
    [InlineData("fp")]                       // kein Mention
    [InlineData("@naudit please fix")]       // kein fp-Token
    [InlineData("@naudit fpx it")]           // Wortgrenze: fpx != fp
    [InlineData("@naudit fp-something")]     // Grund muss durch Whitespace getrennt sein, nicht direkt anschließen
    [InlineData("thanks @naudit fp")]        // Mention nicht am Zeilenanfang
    public void TryParse_returnsNull_forNonCommand(string? body)
        => Assert.Null(FpReplyCommand.TryParse(body));
}
