using Naudit.Core.Models;
using Naudit.Core.Review;
using Xunit;

namespace Naudit.Tests;

public class DiffParserTests
{
    [Fact]
    public void Parse_addedLines_haveNullOldLine_andSequentialNewLines()
    {
        var changes = new[] { new CodeChange("src/Foo.cs", "@@ -0,0 +1,2 @@\n+line1\n+line2") };

        var map = DiffParser.Parse(changes)["src/Foo.cs"];

        Assert.Null(map[1]);
        Assert.Null(map[2]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void Parse_contextLines_carryOldAndNewLine()
    {
        // @@ -1,2 +1,3 @@ : ctx(old1/new1), +new(new2), ctx2(old2/new3)
        var changes = new[] { new CodeChange("src/Foo.cs", "@@ -1,2 +1,3 @@\n ctx\n+new\n ctx2") };

        var map = DiffParser.Parse(changes)["src/Foo.cs"];

        Assert.Equal(1, map[1]);   // Kontextzeile: old 1
        Assert.Null(map[2]);       // hinzugefügt
        Assert.Equal(2, map[3]);   // Kontextzeile: old 2
    }

    [Fact]
    public void Parse_deletedLines_areNotCommentable()
    {
        var changes = new[] { new CodeChange("src/Foo.cs", "@@ -1,2 +1,1 @@\n ctx\n-removed") };

        var map = DiffParser.Parse(changes)["src/Foo.cs"];

        Assert.Equal(1, map[1]);   // nur die Kontextzeile
        Assert.Single(map);
    }

    [Fact]
    public void Parse_multipleHunks_continueNumbering()
    {
        var changes = new[] { new CodeChange("src/Foo.cs", "@@ -1,1 +1,1 @@\n+a\n@@ -10,1 +10,1 @@\n+b") };

        var map = DiffParser.Parse(changes)["src/Foo.cs"];

        Assert.Null(map[1]);
        Assert.Null(map[10]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void Parse_multipleFiles_areKeyedByPath()
    {
        var changes = new[]
        {
            new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x"),
            new CodeChange("b.cs", "@@ -0,0 +1,1 @@\n+y"),
        };

        var result = DiffParser.Parse(changes);

        Assert.True(result.ContainsKey("a.cs"));
        Assert.True(result.ContainsKey("b.cs"));
    }
}
