using Microsoft.Extensions.AI;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Xunit;

namespace Naudit.Tests;

public class PromptBuilderTests
{
    [Fact]
    public void Build_putsSystemPromptFirst_andEmbedsDiffsAndPaths()
    {
        var request = new ReviewRequest("1", 42, "Add feature X");
        var changes = new[]
        {
            new CodeChange("src/Foo.cs", "@@ -1 +1 @@\n-old\n+new"),
            new CodeChange("src/Bar.cs", "@@ -2 +2 @@\n+added"),
        };

        var messages = PromptBuilder.Build("SYSTEM-PROMPT-MARKER", request, changes);

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal("SYSTEM-PROMPT-MARKER", messages[0].Text);
        Assert.Equal(ChatRole.User, messages[1].Role);
        Assert.Contains("Add feature X", messages[1].Text);
        Assert.Contains("src/Foo.cs", messages[1].Text);
        Assert.Contains("+new", messages[1].Text);
        Assert.Contains("src/Bar.cs", messages[1].Text);
    }
}
