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

    [Fact]
    public void Build_annotatesDiffLines_withNewFileLineNumbers()
    {
        var request = new ReviewRequest("1", 42, "Add feature X");
        var changes = new[] { new CodeChange("src/Bar.cs", "@@ -2,1 +2,2 @@\n ctx\n+added") };

        var messages = PromptBuilder.Build("SYS", request, changes);

        // Kontextzeile ist New-File-Zeile 2, hinzugefügte Zeile ist New-File-Zeile 3.
        Assert.Contains("2  ctx", messages[1].Text);
        Assert.Contains("3 +added", messages[1].Text);
    }

    [Fact]
    public void Build_contentLineLookingLikeFileHeader_isStillNumbered()
    {
        // "+++ added" innerhalb des Hunks ist eine hinzugefügte Inhaltszeile, kein Datei-Header.
        var request = new ReviewRequest("1", 42, "x");
        var changes = new[] { new CodeChange("q.sql", "@@ -1,1 +1,2 @@\n ctx\n+++ added") };

        var messages = PromptBuilder.Build("SYS", request, changes);

        Assert.Contains("2 +++ added", messages[1].Text);
    }

    [Fact]
    public void Build_rendersFindings_withScopeLabels()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("src/Foo.cs", "@@ +1 @@") };
        var findings = new[]
        {
            new ScanFinding("opengrep", FindingCategory.Sast, FindingSeverity.High, "sqli", "rule.sqli", "src/Foo.cs", 42) { InDiff = true },
            new ScanFinding("trivy", FindingCategory.Sca, FindingSeverity.Critical, "Newtonsoft.Json 9.0.1", "CVE-2024-1", "packages.lock.json") { InDiff = false },
        };

        var text = PromptBuilder.Build("SYS", request, changes, findings)[1].Text!;

        Assert.Contains("## SAST", text);
        Assert.Contains("[HIGH][in diff] opengrep", text);
        Assert.Contains("src/Foo.cs:42", text);
        Assert.Contains("## Dependency / SCA", text);
        Assert.Contains("[CRITICAL][pre-existing] trivy", text);
        Assert.DoesNotContain("No tool findings.", text);
        // Category order is mandated: Dependency / SCA BEFORE SAST
        Assert.True(text.IndexOf("## Dependency / SCA") < text.IndexOf("## SAST"));
    }

    [Fact]
    public void Build_withoutFindings_saysNoToolFindings()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ +1 @@") };

        var text = PromptBuilder.Build("SYS", request, changes)[1].Text!;

        Assert.Contains("No tool findings.", text);
    }

    [Fact]
    public void Build_withNullLocationAndRule_rendersCleanLine()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ +1 @@") };
        var findings = new[]
        {
            new ScanFinding("sometool", FindingCategory.Sast, FindingSeverity.Info, "a message"),
        };

        var text = PromptBuilder.Build("SYS", request, changes, findings)[1].Text!;

        // Null RuleId and FilePath should produce a clean line with no dangling separators
        Assert.Contains("[INFO][pre-existing] sometool → a message", text);
        Assert.DoesNotContain("sometool · ", text);
    }

    [Fact]
    public void DefaultSystemPrompt_groundsToolchain()
    {
        Assert.Contains("do NOT flag", PromptBuilder.DefaultSystemPrompt);
        Assert.Contains("grounding", PromptBuilder.DefaultSystemPrompt);
    }
}
