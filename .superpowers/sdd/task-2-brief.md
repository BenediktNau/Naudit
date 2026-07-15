## Task 2: PromptBuilder tool-guidance block

**Files:**
- Modify: `src/Naudit.Core/Review/PromtBuilder.cs`
- Modify: `src/Naudit.Core/Review/ReviewService.cs`
- Modify: `tests/Naudit.Tests/PromtBuilderTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `PromptBuilder.Build(string, ReviewRequest, IReadOnlyList<CodeChange>, IReadOnlyList<ScanFinding>?, ReviewContext?, bool toolsAvailable = false)` — new trailing optional param.

- [ ] **Step 1: Write the failing tests**

In `tests/Naudit.Tests/PromtBuilderTests.cs`, add:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~PromtBuilderTests"`
Expected: BUILD FAIL — `Build` has no `toolsAvailable` parameter.

- [ ] **Step 3: Add the parameter and guidance block**

In `src/Naudit.Core/Review/PromtBuilder.cs`, change the `Build` signature:

```csharp
    public static IList<ChatMessage> Build(
        string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes,
        IReadOnlyList<ScanFinding>? findings = null, ReviewContext? context = null,
        bool toolsAvailable = false)
```

Then, inside `Build`, after `AppendFindings(sb, findings ?? []);` and before the `return`, add:

```csharp
        AppendToolGuidance(sb, toolsAvailable);
```

And add the helper method (below `AppendFindings`):

```csharp
    // Nur wenn dem Review Tools angeboten werden: knapper Hinweis, WANN das Docs-Werkzeug sinnvoll ist.
    // Ohne Hinweis ruft das Modell das Tool nie oder ständig — beides verbrennt Budget.
    private static void AppendToolGuidance(StringBuilder sb, bool toolsAvailable)
    {
        if (!toolsAvailable)
            return;
        sb.AppendLine();
        sb.AppendLine("# Tools available");
        sb.AppendLine("You can call a tool to fetch current documentation for a library (Context7). " +
            "Use it when the diff uses an API you are unsure about, rather than guessing against possibly-outdated knowledge. " +
            "Do not use it for well-known stdlib or trivial code. After any tool use, still respond with the required review JSON.");
    }
```

- [ ] **Step 4: Pass the flag from `ReviewService`**

In `src/Naudit.Core/Review/ReviewService.cs`, the tool fetch now happens before `PromptBuilder.Build`. Move the tool fetch above the `messages` line and pass `hasTools`. Replace this block:

```csharp
        var messages = PromptBuilder.Build(options.SystemPrompt, redRequest, redChanges, redFindings, redContext);

        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        // MCP-Tools (leer ⇒ Feature aus): identischer Single-Shot. Nicht-leer ⇒ agentischer Loop
        // über den Function-Invocation-Wrapper des Clients (Infrastructure).
        var tools = await toolProvider.GetToolsAsync(request, ct);
        if (tools.Count > 0)
            chatOptions.Tools = [.. tools];
```

with:

```csharp
        // MCP-Tools (leer ⇒ Feature aus): identischer Single-Shot. Nicht-leer ⇒ agentischer Loop
        // über den Function-Invocation-Wrapper des Clients (Infrastructure) + Hinweis im Prompt.
        var tools = await toolProvider.GetToolsAsync(request, ct);
        var messages = PromptBuilder.Build(options.SystemPrompt, redRequest, redChanges, redFindings, redContext,
            toolsAvailable: tools.Count > 0);

        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        if (tools.Count > 0)
            chatOptions.Tools = [.. tools];
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~PromtBuilderTests|FullyQualifiedName~ReviewServiceTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Core/Review/PromtBuilder.cs src/Naudit.Core/Review/ReviewService.cs tests/Naudit.Tests/PromtBuilderTests.cs
git commit -m "feat(review): Prompt-Guidance fürs Docs-Werkzeug (nur wenn Tools angeboten)"
```

---

