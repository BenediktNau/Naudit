# Review context enrichment (workspace-based) — Design

*2026-07-07 · Naudit*

## Problem

The reviewer LLM today sees only the MR/PR title, the per-file unified diffs and the SAST/SCA
grounding findings (`PromptBuilder.Build`). It has **zero surrounding code** — not even the full
changed file. Three levels of context are missing:

1. **Environment** — what does the changed function/class actually do (its full body, not 3
   context lines)?
2. **Usages** — where is the new/changed symbol called, i.e. how does it plug into the existing
   flow?
3. **Overview** — what overall process/architecture does the change live in?

The real-world test against abrechner PR #584 confirmed the cost: CodeRabbit found two real majors
on the same diff that Naudit missed, both requiring repo context beyond the diff.

**Non-goals:** feeding the whole codebase into the prompt (doesn't scale) and requiring the target
project to run any tooling (code-graph generators à la Graphify). Naudit must stay drop-in: webhook
in, review out.

## Decisions (settled during brainstorming, 2026-07-07)

1. **Works with every `IChatClient` provider** — context is collected deterministically by Naudit
   and packed into the existing single-shot prompt. No function calling, no agentic loop.
2. **All three context levels**, priority **environment > usages > overview** when the budget bites.
3. **Language-agnostic heuristics** (regex + indentation), tuned for precision over recall: a
   missed symbol is fine, spam is not. Roslyn/tree-sitter precision is a later stage, not the POC.
4. **Configurable budget**, moderate default (~40k chars extra).
5. **Source is the existing `IWorkspaceProvider` checkout** (shallow clone of the MR/PR head that
   SAST already uses) — the "code graph" is implicit, per-review and throw-away; the target repo
   needs nothing installed.

## Architecture

Same seam pattern as `IPromptRedactor` / `ISastAnalyzer`: interface + models in Core,
implementation in Infrastructure, selection/config in `AddNauditInfrastructure`. Core keeps
depending on MEAI abstractions only.

### Core additions

```csharp
// Naudit.Core.Abstractions
/// <summary>Schneidet Review-Kontext (Umgebung, Call-Sites, Repo-Überblick) aus dem Workspace.</summary>
public interface IContextCollector
{
    Task<ReviewContext> CollectAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default);
}

// Naudit.Core.Models
public sealed record ReviewContext(
    IReadOnlyList<FileEnvironment> Environments,
    IReadOnlyList<SymbolUsage> Usages,
    string? Overview)
{
    public static readonly ReviewContext Empty = new([], [], null);
}

/// <summary>Umgebender Code einer geänderten Datei: ganze Datei oder Block-Ausschnitt.</summary>
public sealed record FileEnvironment(string FilePath, int StartLine, string Content, bool IsFullFile);

/// <summary>Eine Verwendungsstelle eines im Diff deklarierten Symbols.</summary>
public sealed record SymbolUsage(string Symbol, string FilePath, int Line, string Snippet);
```

`ReviewOptions` gains a `Context` sub-options class (bound from `Naudit:Review:Context`, same
mechanism as `Gate`):

```csharp
public sealed class ReviewContextOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxChars { get; set; } = 40_000;      // Gesamtbudget für die Kontext-Sektion
    public int FullFileMaxLines { get; set; } = 400; // ≤ → ganze Datei, > → Block-Heuristik
    public int UsageSnippetLines { get; set; } = 3;  // ± Zeilen um eine Call-Site
    public int MaxUsagesPerSymbol { get; set; } = 5;
    public int MaxTreeDepth { get; set; } = 3;
    public int ReadmeMaxLines { get; set; } = 50;
}
```

### Infrastructure addition

`src/Naudit.Infrastructure/Context/WorkspaceContextCollector.cs` — the only implementation for
now. Registered unconditionally in `AddNauditInfrastructure`; `ReviewService` skips the call when
`Context.Enabled = false` (no Null impl needed — the service must consult the flag anyway to
decide whether a checkout is required at all).

## Request-flow changes (`ReviewService`)

Today the workspace checkout happens only inside `CollectFindingsAsync` when analyzers are
configured. New shape:

- **Checkout when** `analyzers.Count > 0 || options.Context.Enabled` — one shared workspace for
  SAST and context, disposed after both ran.
- **Checkout failure** → degrade exactly like today: no findings **and** empty context, review
  proceeds diff-only. **Collector exception** → empty context (logged in Infrastructure), never
  fails the review.
- **Redaction:** every piece of context content (environment blocks, usage snippets, overview)
  passes through `IPromptRedactor` before `PromptBuilder.Build` — same rule as diffs/findings/title:
  nothing reaches the LLM unredacted.

## Prompt changes (`PromptBuilder`)

`Build` gains an optional `ReviewContext` parameter. Rendering order: title → diffs →
**context section** → findings section. Empty context (or all-empty parts) renders nothing —
prompt is byte-identical to today.

```text
# Repository context (read-only grounding from the checked-out repo)

## Surrounding code
### src/Foo/Bar.cs (full file)          — or: (lines 120–180)
<fenced code block>

## Usages of changed symbols
### `DoThing` — src/Baz/Qux.cs:42
<fenced snippet, ± UsageSnippetLines>

## Repository overview
Directory tree (depth ≤ 3) + first lines of README.
```

System-prompt addition (appended to `DefaultSystemPrompt`): the context is **read-only
grounding** — use it to judge the change, but report findings **only** on the diff lines shown
with line numbers; do not review unchanged context code for its own sake.

## Extraction heuristics (`WorkspaceContextCollector`)

1. **Environment** — per changed file (skip files missing in the workspace, i.e. deleted):
   - file ≤ `FullFileMaxLines` → the **whole file** (robust, no heuristic risk);
   - larger → per diff hunk, expand to the **enclosing block**: scan backwards to the nearest
     declaration-looking line with lower indentation, forwards until indentation falls back to
     that level; fallback: fixed ±30-line window. Overlapping ranges are merged; each excerpt
     carries its real start line.
2. **Usages** — extract symbol names **declared on added (`+`) diff lines** via a regex catalogue
   (keyword declarations `def|function|func|fn|sub NAME`, type declarations
   `class|interface|struct|record|enum|trait NAME`, C-family method signatures). Then a
   word-boundary text search across workspace text files — excluding the declaring file, binary
   files, and vendor/build dirs (`.git`, `bin`, `obj`, `node_modules`, `dist`, `target`,
   `vendor`, …; per-file size cap) — capped at `MaxUsagesPerSymbol` sites, each with
   ±`UsageSnippetLines` lines.
3. **Overview** — directory tree (depth ≤ `MaxTreeDepth`, same exclusion list, entry cap) plus the
   first `ReadmeMaxLines` lines of a root `README.*` if present.

## Budget

The collector assembles in priority order **environments → usages → overview** into `MaxChars`.
A block that doesn't fit fully is truncated with an explicit `… [truncated by budget]` marker;
everything after the budget is dropped. Deterministic: same input → same context.

## Configuration

All under `Naudit:Review:Context` (see options class above for defaults). `Enabled` defaults to
**true** — the feature is the point of Naudit's next quality step. Note for cost-sensitive
deployments: with SAST off, enabling context introduces one shallow clone per review; set
`Naudit:Review:Context:Enabled=false` to restore today's exact behaviour and prompt.

## Testing

- **`WorkspaceContextCollector` unit tests** on temp-dir fixtures, no network: full-file
  threshold, block-expansion heuristic (incl. fallback window), symbol-regex catalogue per
  language family, usage search exclusions (declaring file, vendor dirs), budget priority order
  and truncation marker, deterministic output.
- **`PromptBuilder` tests:** renders the context section; empty context ⇒ prompt unchanged.
- **`ReviewService` tests** (fakes, incl. new `FakeContextCollector`): context content passes the
  redactor; `Enabled=false` and no analyzers ⇒ no checkout attempted; collector throws ⇒ review
  still completes diff-only.

## Out of scope (future stages, same seam)

- **Cached "repo map"** — an LLM-generated architecture summary per repo, cached and prepended
  (best answer to the overview level; needs persistence + invalidation).
- **Agentic lookup** — tools (read file, grep) for capable providers as opt-in.
- **tree-sitter** precision if the heuristics prove too fuzzy in practice.
