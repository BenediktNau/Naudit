# Offline code-review benchmark (withmartian) — Design

*2026-08-04 · Naudit*

## Problem

The thesis needs a defensible, quantitative comparison of Naudit against other code-review
agents. Every measurement so far has been a single-PR head-to-head against CodeRabbit on
Naudit's own repository — useful as a qualitative signal, but not a number anyone can rank.

[`withmartian/code-review-benchmark`](https://github.com/withmartian/code-review-benchmark)
(the `offline/` half) is an open replication of the evaluation frameworks Augment and Greptile
publish: **50 pull requests** across five large open-source codebases (sentry, grafana,
keycloak, discourse, cal.com), each annotated with **human-verified golden comments** as ground
truth. An LLM judge decides, per candidate finding, whether it describes the same underlying
issue as a golden comment, and precision/recall follow.

Two properties make it worth using rather than building something ourselves:

1. `results/benchmark_data.json` already contains the reviews **and** judged results of **41
   tools** — augment, coderabbit, greptile, copilot, cursor bugbot, qodo, devin, claude-code,
   graphite, propel, sourcery and more — under **three judge models**. Adding Naudit means
   running the pipeline for one tool, not forty-two.
2. The judge, the prompts and the metric are fixed and inspectable, so the comparison does not
   rest on our own scoring choices.

The obstacle is the upstream **collection** path. `step0_fork_prs.py` re-creates each of the 50
PRs as its own GitHub repository in an organisation where the tool under test is installed —
full clone plus full push per PR, which for these five repos is roughly **54 GB in each
direction** and 50 public repositories. The tool then reviews PR #1 in each repo and
`step1_download_prs.py` harvests the comments. Everything downstream of that harvest is cheap.

This design keeps the benchmark's measurement untouched and replaces only its collection step.

## Decisions (settled during brainstorming, 2026-08-04)

1. **Local capture instead of forked PRs.** Naudit reads each benchmark PR read-only through the
   real `GitHubPlatform` and its review is captured to disk instead of posted. No GitHub
   organisation, no GitHub App, no forks, no write access anywhere.

   This is measurement-neutral, and that is a checked claim rather than an assumption:
   `PostReviewAsync` sends the summary and *all* inline comments in a single GitHub review call,
   which GitHub rejects wholesale (422) if any comment sits on a line outside the diff — the one
   thing that could differ between posting and capturing. It cannot occur, because
   `ReviewService` builds the commentable-line set via `DiffParser.Parse` and only promotes a
   finding to an inline comment when its file and line appear in that set; everything else
   becomes an `OrphanComment` and lands in the summary (`ReviewService.cs:99-120`). The set of
   comments is therefore identical either way.

2. **Fixture source is the golden entry's `url` field, verbatim.** 35 of the 50 entries point at
   the upstream PR (`getsentry/sentry/pull/93824`); the remaining **15 point at prepared PRs in
   a third-party organisation** `ai-code-review-evaluation` — ten discourse entries whose
   `original_url` is a *commit* rather than a PR, one keycloak entry, and four sentry entries of
   which **two carry no `original_url` at all**. There is no upstream PR to review for those.
   `step0` clones whatever `url` names, so reading `url` uniformly means Naudit reviews the same
   fixture the other 41 tools reviewed. All three `ai-code-review-evaluation` repositories were
   verified public, their PRs open, and `refs/pull/N/head` present.

3. **Reviews run on the Claude Code subscription**, not a metered API key: provider `ClaudeCode`,
   model alias `opus`, authenticated by `CLAUDE_CODE_OAUTH_TOKEN`. The provider is a plain swap
   behind MEAI — still one pass over the diff (`--max-turns 1 --tools ""`) — and repository
   context and the architecture profile are prompt sections rather than tools, so review quality
   does not depend on the provider.

4. **Judging runs through OpenRouter with the benchmark's own judge models**, so Naudit's numbers
   land in the existing result directories next to the 41 other tools. `withmartian` itself is
   enterprise-sales only (no self-serve key), and the pipeline's `MARTIAN_*` variables are a
   plain `AsyncOpenAI(api_key=…, base_url=…)` — any OpenAI-compatible endpoint works.

5. **Ship-default pipeline configuration.** Repository context on, architecture profile on,
   redaction on, review memory empty, SAST off (`Naudit:Sast:Enabled=false`). This measures
   Naudit as delivered rather than a tuned variant.

6. **Nothing is published.** Results stay in `results/` and the dashboard is a local HTML file.

## What the benchmark does (unchanged)

| Step | Script | Role |
|---|---|---|
| 1 | `step1_download_prs.py` | Harvest tool comments from the fixture PRs → `benchmark_data.json`. **Replaced locally** (see below). |
| 2 | `step2_extract_comments.py` | All of a tool's comment bodies for one PR are concatenated and sent to an LLM, which extracts the distinct issues as candidates. Line-bound comments are *not* short-circuited into candidates — they go through the same extraction. |
| 2.5 | `step2_5_dedup_candidates.py` | An LLM groups candidates expressing the same concern so a tool is not penalised for repeating itself in summary and inline form. |
| 3 | `step3_judge_comments.py` | Cartesian product: every golden comment × every candidate of that PR, one small LLM call each → TP/FP/FN, precision, recall. |
| 4 | `analysis/benchmark_dashboard.py` | Regenerates dashboard JSON + HTML. |
| — | `step4_export_by_tool.py --tool naudit` | Optional: an `.xlsx` of Naudit's findings with the judge's verdict and reasoning per finding — the artefact worth putting in the thesis appendix. |

Steps 2, 2.5 and 3 write into `results/{MARTIAN_MODEL with "/" → "_"}/`. The existing
directories are `anthropic_claude-opus-4-5-20251101`,
`anthropic_claude-sonnet-4-5-20250929` and `openai_gpt-5.2`.

Ground truth as used by the judge is the `golden_comments` already stored in
`benchmark_data.json` — **137 comments across the 50 PRs** (the `golden_comments/*.json` sources
sum to 136; one sentry entry carries one more comment in the merged file). The import must not
touch these.

## Architecture

Three pieces. Nothing in `src/` changes.

### 1. Capture harness — `tools/Naudit.Benchmark/`

A .NET console project added to `Naudit.slnx` (and *not* referenced by the `Dockerfile`, which
continues to build only `Naudit.Web`). It builds the real service graph through
`AddNauditInfrastructure` and decorates a single seam:

```csharp
// Reads go to the real platform; the post is captured instead of sent.
sealed class CapturingGitPlatform(IGitPlatform inner, ReviewCapture capture) : IGitPlatform
{
    public Task<IReadOnlyList<CodeChange>> GetChangesAsync(...)  => inner.GetChangesAsync(...);
    public Task<RepoCheckoutInfo>          GetCheckoutAsync(...) => inner.GetCheckoutAsync(...);

    public Task<IReadOnlyList<PostedComment>> PostReviewAsync(
        ReviewRequest request, string summaryMarkdown,
        IReadOnlyList<InlineComment> comments, ReviewVerdict verdict, CancellationToken ct)
    {
        capture.Record(request, summaryMarkdown, comments, verdict);
        return Task.FromResult<IReadOnlyList<PostedComment>>(
            comments.Select(_ => new PostedComment(null, null)).ToList());
    }
}
```

Returning index-aligned null ids matches the documented best-effort contract, so
`ReviewService`'s audit path behaves exactly as it does when GitHub's id lookup fails.

The runner:

1. reads the 50 entries from `golden_comments/*.json`, parsing `owner/repo/number` out of `url`;
2. builds a `ReviewRequest` per entry with `ProjectId = "<owner>/<repo>"`, `MergeRequestIid =
   <number>`, `Title = <pr_title>`, `Trigger = Ci`;
3. calls `ReviewService.ReviewAsync` sequentially, with a configurable pause between reviews;
4. appends each captured review to `naudit-reviews.json` and a per-review diagnostic record.

`ProjectId` is the fixture repository, so the run spans **7 distinct projects** rather than 50:

| PRs | Fixture repository |
|---:|---|
| 10 | `ai-code-review-evaluation/discourse-graphite` |
| 10 | `calcom/cal.com` |
| 10 | `grafana/grafana` |
| 9 | `keycloak/keycloak` |
| 6 | `getsentry/sentry` |
| 4 | `ai-code-review-evaluation/sentry-greptile` |
| 1 | `ai-code-review-evaluation/keycloak-greptile` |

The architecture profile is therefore distilled seven times and served from the hash cache
afterwards — cheaper, and consistent across the PRs of one repository.

`Trigger = Ci` keeps the roundtrip limit out of the way; it would not bite at one review per PR
anyway, but the intent should be explicit.

### 2. Import — `tools/benchmark/import_reviews.py`

Merges `naudit-reviews.json` into `results/benchmark_data.json` as a review entry with
`tool: "naudit"`, in exactly the shape `step1` produces:

```json
{"tool": "naudit", "repo_name": "...", "pr_url": "...",
 "review_comments": [{"path": "...", "line": 42, "body": "...", "created_at": "..."}]}
```

The summary becomes one entry with `path: null, line: null` (matching how `step1` records
top-level review bodies), each inline comment one entry with its path and line. Existing keys —
above all `golden_comments` — are read and written back untouched; the script refuses to run if
a `naudit` entry already exists unless `--force` is given.

It also refuses to run on an **incomplete** run: every key of `benchmark_data.json` must have a
record. The benchmark computes recall per tool over all PRs of the target file, so importing 30
of 50 would score Naudit over 30 PRs while the other 41 tools are scored over 50 — and the
missing ones would be exactly the hard ones, making Naudit look better than it is.
`--allow-partial` overrides this and prints a loud warning saying the number is not comparable.

### 3. Judge routing — `MARTIAN_MODEL_ENDPOINT`

`MARTIAN_MODEL` decides the output directory; the same string is sent to the endpoint. OpenRouter
serves the two Claude judges under different ids, so the two roles are split by a new optional
variable, two lines in each of `step2`, `step2_5` and `step3`:

```python
self.model = os.environ.get("MARTIAN_MODEL_ENDPOINT") or os.environ.get("MARTIAN_MODEL", "openai/gpt-4o-mini")
```

`get_model_dir()` keeps reading `MARTIAN_MODEL` and is not touched. Mapping:

| `MARTIAN_MODEL` (directory) | `MARTIAN_MODEL_ENDPOINT` (call) |
|---|---|
| `anthropic/claude-sonnet-4-5-20250929` | `anthropic/claude-sonnet-4.5` |
| `anthropic/claude-opus-4-5-20251101` | `anthropic/claude-opus-4.5` |
| `openai/gpt-5.2` | *(unset — the id is identical)* |

Sonnet 4.5 and Opus 4.5 each have exactly one snapshot (`20250929`, `20251101`), so this renames
a model, it does not substitute one.

## Run configuration

Environment for the harness (user-secrets or env; nothing touches the deployed instance):

| Key | Value |
|---|---|
| `Naudit:Git:Platform` | `GitHub` |
| `Naudit:GitHub:Token` | fine-grained token, **read-only, public repositories** |
| `Naudit:GitHub:WebhookSecret` | any non-empty value (required by `SetupStatus`, unused here) |
| `Naudit:Ai:Provider` | `ClaudeCode` |
| `Naudit:Ai:Model` | `opus` |
| `CLAUDE_CODE_OAUTH_TOKEN` | from `claude setup-token` |
| `Naudit:Sast:Enabled` | `false` (default) |
| `Naudit:Db:*` | a scratch SQLite file under the harness output directory |

A read-only token is the safety net that matters: the harness never calls a write endpoint, and
a token without write scope means a bug cannot either.

Judge (`offline/.env`): `MARTIAN_API_KEY` = OpenRouter key,
`MARTIAN_BASE_URL=https://openrouter.ai/api/v1`, plus the model pair per run.

## Error handling, resumption, and fail-open detection

- **Resumption.** `naudit-reviews.json` is the state: PRs already present are skipped. A
  subscription rate limit pauses the run instead of losing it, and a review that failed is
  retried on the next start rather than being recorded as "found nothing".
- **Fail-open is a defect here.** Naudit deliberately continues when the checkout, the
  architecture profile or a SAST analyzer fails. In production that is right; in a benchmark it
  silently produces a worse review with no trace. Worse, most of those paths are not even logged:
  `GitHubPlatform.GetCheckoutAsync` throws through `EnsureSuccessStatusCode`,
  `ReviewService.GatherGroundingAsync` swallows it, `WorkspaceContextCollector` has no logger at
  all, and the audit sink logs only its success case. The harness therefore records per review:
  whether the checkout was requested and whether it **failed**, the actually checked-out commit,
  whether the prompt carried the repository-context and the architecture-profile section, prompt
  and completion token counts, the number of changed files, the wall-clock duration, and every
  `Warning`/`Error` the pipeline did log.

  Three decorators supply this, all in `tools/`, none touching `src/`: `IGitPlatform` (checkout
  outcome, changed-file count, capture instead of post), `IChatClient` (what the outgoing prompt
  contained, token counts) and `IWorkspaceProvider` (the resolved head commit, failed clone). The
  chat decorator has to tell the architecture-profile **distillation** call apart from the review
  call, because both go through the same global client: a call counts as the review only if its
  user text carries both headings `PromptBuilder.Build` always writes (`# Merge Request: ` and
  `# Static-analysis & dependency findings`); the distillation prompt is nothing but repository
  documents and cannot carry both. The last such call wins, since the review call follows the
  grounding.

  Any review with a failed checkout, a missing context section, a missing profile section, a
  pipeline warning or an error is reported at the end and re-run rather than imported — and
  `import_reviews.py` refuses those same records, treating a missing diagnostic field as a
  rejection rather than a pass.
- **A failed review is not an empty review.** Only reviews that completed are written; the
  importer refuses to import a run whose diagnostic log contains unresolved failures.
- **Judging is incremental.** `step3` tracks completed `(PR, tool)` pairs, so an interrupted
  judge run resumes.

## Validation

One PR runs end to end — capture, import, `step2`, `step2_5`, `step3` — before the other 49
start. It settles four things in one pass: the decorator captures instead of posting; the
`url`-derived `ProjectId` resolves for both fixture flavours; OpenRouter accepts the pipeline's
`temperature=0.0` (the GPT-5 family rejects non-default temperature, which matters if gpt-5.2 is
added later); and the directory mapping puts results where the other 41 tools already are.

Expected cost and runtime for the full run: judging is the only monetary item at roughly **2.50 $
for Sonnet 4.5 and 4 $ for Opus 4.5** (~700 judge calls plus ~50 extraction and ~50 dedup calls
per judge model, at ~300k input / ~100k output tokens). Reviews are covered by the subscription.
The 50 shallow checkouts of large repositories are the dominant wall-clock cost at roughly one to
three minutes each.

## Deviations from the upstream pipeline

Three, all to be stated in the thesis:

1. **Collection is local.** Naudit's comments are captured through a decorating `IGitPlatform`
   rather than harvested from forked PRs by `step1`. Review inputs are identical — same
   `/pulls/{n}/files` response, same `refs/pull/N/head` tree — and the captured comment set is
   provably the same as the posted one (decision 1).
2. **Judge model names are mapped** to OpenRouter's ids, because the benchmark's own router has
   no self-serve access. Same models, same snapshots.
3. **The fixture is not frozen.** The comparison tools reviewed *pushed snapshots*, not the live
   pull requests. Each `pr_url` in `benchmark_data.json` carries the push date in its repository
   name (`keycloak__keycloak__augment__PR37634__20260122/pull/1`), and those dates run from
   **2026-01-22 to 2026-04-06** — the earliest and largest cohort (augment, coderabbit, copilot,
   greptile, bugbot, propel, baz: 350 reviews) is 2026-01-22, the later tool generations were
   pushed in March and April. Naudit reads the upstream pull request *today*, months after every
   one of them. For a pull request that is still open and has received further commits since, the
   diff and the head Naudit sees are not the ones the other tools saw — and the tools do not even
   share one state among themselves. This cannot be avoided without the fork route (out of scope),
   so it is mitigated by recording, per review, the head commit Naudit actually checked out
   (`headSha` in `naudit-reviews.json`, read from `.git/HEAD` of the workspace — the clone URL
   carries the token and is deliberately never recorded). That makes the divergence auditable
   after the fact instead of invisible.

Extraction, deduplication, judge prompt and metric are untouched.

## Limitations to report

- **Training-data leakage.** The 50 PRs come from prominent repositories and may appear in any
  model's training data, Naudit's Opus session included. This affects every tool in the
  comparison equally, but it means the result is not a statement about performance on unseen
  code. The benchmark's own README lists this first.
- **Human-curated ground truth.** A real finding that no human annotator recorded counts as a
  false positive here. Precision is therefore a lower bound.
- **Judge variance.** The judge is an LLM; the benchmark mitigates this with fixed prompts and by
  reporting the judge model. Running both Claude judges lets the thesis show whether Naudit's
  placement is stable across judges.
- **Single run.** No repeated sampling, so run-to-run variance of Naudit itself is not
  characterised.
- **The fixture is not frozen.** Same fact as deviation 3, and it belongs in the limitations too:
  Naudit reviews today's state of each pull request, the other tools reviewed snapshots pushed
  between 2026-01-22 and 2026-04-06. For a still-open PR that has grown since, the two are not the
  same input, and the golden comments were annotated against the older one. The recorded `headSha`
  per review lets the thesis state which state was measured, but it does not remove the
  divergence — nor the fact that the 41 comparison tools are themselves spread over three months
  of snapshots.
- **No pagination.** `GitHubPlatform.GetChangesAsync` fetches a single page (`per_page=100`) — a
  deliberate POC limit of the product, not of the harness. A pull request with more than 100
  changed files would be reviewed on a truncated diff, and the golden comments for the omitted
  files could never be found. The runner records the changed-file count per review and lists every
  PR that hit a full page separately at the end; those are *not* re-run (a re-run would see the
  same thing) but reported as a limitation. The count is a lower bound: files without a patch
  (binary or too large) are already filtered out, so a full page containing binaries shows up
  below 100.

## Testing

- `CapturingGitPlatform` gets unit tests against `FakeGitPlatform`: reads delegate through,
  `PostReviewAsync` records and returns index-aligned null ids, and no write reaches the inner
  platform.
- The URL parser gets a test over all 50 real `url` values, including the
  `ai-code-review-evaluation` flavour, asserting owner/repo/number for each.
- The importer gets a test asserting that `golden_comments` and foreign tool entries survive a
  round trip byte-identical.
- The existing suite must stay green; run it with `DOTNET_USE_POLLING_FILE_WATCHER=1`.

## Out of scope

- The fork route (`step0` or an API-based variant) — reconsider only if the local numbers are
  contested.
- The `online/` half of the benchmark, which avoids training-data leakage by using fresh PRs.
- gpt-5.2 as a third judge — a later addition for ~2 $ with no re-run of anything, since its
  OpenRouter id already matches the published directory name.
- Contributing Naudit's results back upstream.
