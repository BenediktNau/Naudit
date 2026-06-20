# CI/CD integration via `POST /review`

As an alternative (or complement) to the webhook, a pipeline can trigger Naudit actively. The
endpoint runs **synchronously**: it reviews the MR/PR inline, posts the comment, and responds
with the verdict.

- **Auth:** header `X-Naudit-Token`, compared against the active platform's `WebhookSecret`
  (`Naudit:GitLab:WebhookSecret` or `Naudit:GitHub:WebhookSecret`). Store it as a masked
  variable in CI.
- **Body:** `{ "projectId": "<id|owner/repo>", "mergeRequestIid": <int>, "title": "<text>" }`.
- **Response:** `200` with `{ "verdict": "approve" | "request_changes" }`. Auth failure ⇒ `401`,
  Naudit/LLM/Git error ⇒ `5xx`. The job fails on `request_changes` **and** on non-2xx
  (`curl -f` covers the latter).

## GitLab CI (`.gitlab-ci.yml`)

```yaml
naudit-review:
  stage: test
  image: alpine:latest
  rules:
    - if: $CI_PIPELINE_SOURCE == "merge_request_event"
  before_script:
    - apk add --no-cache curl jq
  script:
    - |
      VERDICT=$(curl -sf -X POST "$NAUDIT_URL/review" \
        -H "X-Naudit-Token: $NAUDIT_TOKEN" -H "Content-Type: application/json" \
        -d "{\"projectId\":\"$CI_PROJECT_ID\",\"mergeRequestIid\":$CI_MERGE_REQUEST_IID,\"title\":\"$CI_MERGE_REQUEST_TITLE\"}" \
        | jq -r '.verdict')
      echo "Naudit verdict: $VERDICT"
      [ "$VERDICT" = "approve" ]
```

Set `NAUDIT_URL` and `NAUDIT_TOKEN` as CI/CD variables (token masked).

## GitHub Actions

```yaml
name: Naudit Review
on:
  pull_request:
    types: [opened, reopened, synchronize]
jobs:
  review:
    runs-on: ubuntu-latest
    steps:
      - name: Naudit code review
        env:
          NAUDIT_URL: ${{ secrets.NAUDIT_URL }}
          NAUDIT_TOKEN: ${{ secrets.NAUDIT_TOKEN }}
        run: |
          VERDICT=$(curl -sf -X POST "$NAUDIT_URL/review" \
            -H "X-Naudit-Token: $NAUDIT_TOKEN" -H "Content-Type: application/json" \
            -d "{\"projectId\":\"${{ github.repository }}\",\"mergeRequestIid\":${{ github.event.pull_request.number }},\"title\":\"${{ github.event.pull_request.title }}\"}" \
            | jq -r '.verdict')
          echo "Naudit verdict: $VERDICT"
          [ "$VERDICT" = "approve" ]
```
