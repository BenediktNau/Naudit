# CI/CD-Integration über `POST /review`

Alternativ (oder ergänzend) zum Webhook kann eine Pipeline Naudit aktiv triggern. Der Endpoint
läuft **synchron**: er reviewt den MR/PR inline, postet den Kommentar und antwortet mit dem Verdict.

- **Auth:** Header `X-Naudit-Token`, verglichen gegen das `WebhookSecret` der aktiven Plattform
  (`Naudit:GitLab:WebhookSecret` bzw. `Naudit:GitHub:WebhookSecret`). In der CI als maskierte
  Variable hinterlegen.
- **Body:** `{ "projectId": "<id|owner/repo>", "mergeRequestIid": <int>, "title": "<text>" }`.
- **Antwort:** `200` mit `{ "verdict": "approve" | "request_changes" }`. Auth-Fehler ⇒ `401`,
  Naudit-/LLM-/Git-Fehler ⇒ `5xx`. Der Job failt bei `request_changes` **und** bei non-2xx
  (`curl -f` deckt Letzteres ab).

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

`NAUDIT_URL` und `NAUDIT_TOKEN` als CI/CD-Variablen setzen (Token maskiert).

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
