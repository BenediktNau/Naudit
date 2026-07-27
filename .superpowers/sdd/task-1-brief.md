### Task 1: `FindingCategory.Dast` + prompt rendering

**Files:**
- Modify: `src/Naudit.Core/Models/ScanFinding.cs:4`
- Modify: `src/Naudit.Core/Review/PromtBuilder.cs:182-184`
- Test: `tests/Naudit.Tests/PromtBuilderTests.cs`

**Interfaces:**
- Produces: `FindingCategory.Dast` (enum member, last); a "DAST (dynamic)" grounding section rendered by `PromptBuilder.Build` when any finding has `Category == FindingCategory.Dast`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Naudit.Tests/PromtBuilderTests.cs` (match the file's existing construction of `PromptBuilder.Build` and its `ScanFinding` args — read the neighbouring tests first; the finding list parameter is the redacted `IReadOnlyList<ScanFinding>`):

```csharp
    [Fact]
    public void Build_rendersDastFindings_underDynamicHeading()
    {
        var findings = new List<ScanFinding>
        {
            new("dast", FindingCategory.Dast, FindingSeverity.High,
                "Reflected XSS at /search?q= — payload echoed unescaped", RuleId: null, FilePath: null, Line: null),
        };

        var messages = PromptBuilder.Build(SystemPrompt, Request, Changes, findings);

        var user = string.Join("\n", messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        Assert.Contains("DAST (dynamic)", user);
        Assert.Contains("Reflected XSS at /search", user);
    }
```

(Use the same `SystemPrompt`/`Request`/`Changes` helpers the other tests in the file use. If `Build`'s findings parameter is optional/positional, mirror an existing findings-based test exactly.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~Build_rendersDastFindings"`
Expected: FAIL — either `FindingCategory.Dast` does not compile (CS0117) or the heading is absent.

- [ ] **Step 3: Add the enum member**

In `src/Naudit.Core/Models/ScanFinding.cs:4`:

```csharp
public enum FindingCategory { Sast, Sca, Secrets, Dast }
```

- [ ] **Step 4: Render the group**

In `src/Naudit.Core/Review/PromtBuilder.cs`, directly after the SAST line (currently line 184):

```csharp
        AppendCategory(sb, "DAST (dynamic)", findings.Where(f => f.Category == FindingCategory.Dast));
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PromtBuilderTests`
Expected: PASS (existing + the new test).

- [ ] **Step 6: Build + full suite**

Run: `dotnet build Naudit.slnx && DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS — 701 (700 + 1).

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Core/Models/ScanFinding.cs src/Naudit.Core/Review/PromtBuilder.cs tests/Naudit.Tests/PromtBuilderTests.cs
git commit -m "feat(dast): FindingCategory.Dast + Prompt-Sektion für dynamische Funde"
```

---

