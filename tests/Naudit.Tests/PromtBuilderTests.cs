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
    public void Build_rendersSecretsFindings_beforeOtherCategories()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ +1 @@") };
        var findings = new[]
        {
            new ScanFinding("opengrep", FindingCategory.Sast, FindingSeverity.Low, "x", "r", "a.cs", 1) { InDiff = true },
            new ScanFinding("gitleaks", FindingCategory.Secrets, FindingSeverity.High, "GitHub Personal Access Token", "github-pat", "a.cs", 2) { InDiff = true },
        };

        var text = PromptBuilder.Build("SYS", request, changes, findings)[1].Text!;

        Assert.Contains("## Secrets", text);
        Assert.Contains("[HIGH][in diff] gitleaks · github-pat", text);
        // Secrets sind am dringlichsten -> vor SAST gerendert.
        Assert.True(text.IndexOf("## Secrets") < text.IndexOf("## SAST"));
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

    [Fact]
    public void Build_rendersContextSection_whenPresent()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ +1 @@") };
        var context = new ReviewContext(
            new[] { new FileEnvironment("src/Foo.cs", 1, "class Foo { }", IsFullFile: true) },
            new[] { new SymbolUsage("DoThing", "src/Bar.cs", 42, "  DoThing();") },
            "Directory tree (depth ≤ 3):\nsrc/");

        var text = PromptBuilder.Build("SYS", request, changes, null, context)[1].Text!;

        Assert.Contains("# Repository context", text);
        Assert.Contains("## Surrounding code", text);
        Assert.Contains("src/Foo.cs (full file)", text);
        Assert.Contains("class Foo { }", text);
        Assert.Contains("## Usages of changed symbols", text);
        Assert.Contains("`DoThing` — src/Bar.cs:42", text);
        Assert.Contains("## Repository overview", text);
        Assert.Contains("Directory tree", text);
    }

    [Fact]
    public void Build_blockEnvironment_showsStartLine()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ +1 @@") };
        var context = new ReviewContext(
            new[] { new FileEnvironment("src/Big.cs", 120, "void M() { }", IsFullFile: false) },
            Array.Empty<SymbolUsage>(), null);

        var text = PromptBuilder.Build("SYS", request, changes, null, context)[1].Text!;

        Assert.Contains("src/Big.cs (from line 120)", text);
    }

    [Fact]
    public void Build_contextContentWithBackticks_usesLongerFence()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ +1 @@") };
        // Umgebung enthält selbst einen ```-Codeblock (z. B. eine geänderte Markdown-Datei).
        var context = new ReviewContext(
            new[] { new FileEnvironment("README.md", 1, "text\n```\ncode\n```\nmore", IsFullFile: true) },
            Array.Empty<SymbolUsage>(), null);

        var text = PromptBuilder.Build("SYS", request, changes, null, context)[1].Text!;

        // Umschließender Fence muss länger sein als der längste ```-Lauf im Inhalt.
        Assert.Contains("````", text);
        // Inhalt bleibt vollständig (nichts läuft aus dem Block).
        Assert.Contains("more", text);
    }

    [Fact]
    public void Build_withoutContext_rendersNoContextSection()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ +1 @@") };

        var text = PromptBuilder.Build("SYS", request, changes)[1].Text!;

        Assert.DoesNotContain("# Repository context", text);
    }

    [Fact]
    public void Build_emptyContext_rendersNoContextSection()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ +1 @@") };

        var text = PromptBuilder.Build("SYS", request, changes, null, ReviewContext.Empty)[1].Text!;

        Assert.DoesNotContain("# Repository context", text);
    }

    [Fact]
    public void DefaultSystemPrompt_mentionsRepositoryContext()
    {
        Assert.Contains("Repository context", PromptBuilder.DefaultSystemPrompt);
    }

    [Fact]
    public void Build_withoutTools_hasNoToolGuidance()
    {
        var msgs = PromptBuilder.Build("SYS", new ReviewRequest("1", 1, "T"),
            [new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);

        Assert.DoesNotContain("Tools available", msgs[1].Text);
    }

    [Fact]
    public void Build_withTools_rendersToolGuidance()
    {
        var msgs = PromptBuilder.Build("SYS", new ReviewRequest("1", 1, "T"),
            [new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")],
            findings: null, context: null, toolsAvailable: true);

        Assert.Contains("Tools available", msgs[1].Text);
        Assert.Contains("documentation", msgs[1].Text);
    }

    [Fact]
    public void Build_withMemory_rendersFalsePositivesAndConventions()
    {
        var request = new ReviewRequest("1", 1, "T");
        var changes = new List<CodeChange> { new("a.cs", "@@ -0,0 +1,1 @@\n+x") };
        var memory = new List<MemoryEntry>
        {
            new(MemoryKind.FalsePositive, "src/Foo/Bar.cs", "Angeblich ungeschlossenes <li>", "Redactor-Artefakt"),
            new(MemoryKind.FalsePositive, null, "Tailwind-4-Syntax ist kein Fehler", null),
            new(MemoryKind.Convention, null, "Wir nutzen bewusst deutsche Code-Kommentare", null),
        };

        var messages = PromptBuilder.Build("SYS", request, changes, memory: memory);
        var user = messages[1].Text;

        Assert.Contains("# Project memory (maintainer guidance)", user);
        Assert.Contains("## Known false positives — do NOT report these or equivalent findings again", user);
        Assert.Contains("- src/Foo/Bar.cs: Angeblich ungeschlossenes <li> (maintainer note: Redactor-Artefakt)", user);
        Assert.Contains("- Tailwind-4-Syntax ist kein Fehler", user);
        Assert.Contains("## Project conventions — respect these when judging the diff", user);
        Assert.Contains("- Wir nutzen bewusst deutsche Code-Kommentare", user);
        // Gedächtnis ist die LETZTE Sektion (näher an der Antwort = höheres Gewicht).
        Assert.True(user.IndexOf("# Project memory", StringComparison.Ordinal)
            > user.IndexOf("# Static-analysis", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_withEmptyMemory_isByteIdenticalToNoMemory()
    {
        var request = new ReviewRequest("1", 1, "T");
        var changes = new List<CodeChange> { new("a.cs", "@@ -0,0 +1,1 @@\n+x") };

        var without = PromptBuilder.Build("SYS", request, changes)[1].Text;
        var withEmpty = PromptBuilder.Build("SYS", request, changes, memory: [])[1].Text;

        Assert.Equal(without, withEmpty);
    }

    [Fact]
    public void DefaultSystemPrompt_mentionsProjectMemory()
        => Assert.Contains("Project memory", PromptBuilder.DefaultSystemPrompt);

    [Fact]
    public void Build_withGuidelines_rendersAuthoritativeSection_beforeMemory()
    {
        var request = new ReviewRequest("7", 1, "Titel");
        var changes = new List<CodeChange> { new("src/A.cs", "@@ -0,0 +1 @@\n+x") };
        var memory = new List<MemoryEntry> { new(MemoryKind.Convention, null, "Konvention X", null) };

        var messages = PromptBuilder.Build(PromptBuilder.DefaultSystemPrompt, request, changes,
            memory: memory, guidelines: "- Webhook endpoints must enqueue and return 200 immediately.");
        var text = string.Join("\n", messages.Select(m => m.Text));

        Assert.Contains("# Project guidelines (distilled from this repository's own documentation; maintainer-curated, authoritative)", text);
        Assert.Contains("Webhook endpoints must enqueue", text);
        // Guidelines-Sektion steht VOR der Memory-Sektion (Memory bleibt zuletzt).
        Assert.True(text.IndexOf("# Project guidelines", StringComparison.Ordinal)
                  < text.IndexOf("# Project memory", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_withoutGuidelines_isByteIdentical()
    {
        var request = new ReviewRequest("7", 1, "Titel");
        var changes = new List<CodeChange> { new("src/A.cs", "@@ -0,0 +1 @@\n+x") };

        var without = string.Join("\n", PromptBuilder.Build(PromptBuilder.DefaultSystemPrompt, request, changes).Select(m => m.Text));
        var withNull = string.Join("\n", PromptBuilder.Build(PromptBuilder.DefaultSystemPrompt, request, changes, guidelines: null).Select(m => m.Text));
        var withBlank = string.Join("\n", PromptBuilder.Build(PromptBuilder.DefaultSystemPrompt, request, changes, guidelines: "   ").Select(m => m.Text));

        Assert.Equal(without, withNull);
        Assert.Equal(without, withBlank);
    }

    [Fact]
    public void DefaultSystemPrompt_containsAltitudeAndSecurityInstructions()
    {
        Assert.Contains("architecture level", PromptBuilder.DefaultSystemPrompt);
        Assert.Contains("Project guidelines", PromptBuilder.DefaultSystemPrompt);
        Assert.Contains("injection surfaces", PromptBuilder.DefaultSystemPrompt);
        Assert.Contains("omit \"line\"", PromptBuilder.DefaultSystemPrompt);
    }
}
