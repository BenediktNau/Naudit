import json
import pytest
from import_reviews import build_review_entry, merge


def record(url="https://github.com/getsentry/sentry/pull/1"):
    return {
        "url": url,
        "review": {
            "projectId": "getsentry/sentry",
            "mergeRequestIid": 1,
            "summary": "Zusammenfassung",
            "verdict": "Approve",
            "comments": [
                {"filePath": "a.cs", "newLine": 5, "body": "Fund",
                 "severity": "High", "confidence": "Medium"},
            ],
        },
        "diagnostics": {"checkoutRequested": True, "warnings": [],
                        "durationSeconds": 12.5, "error": None},
    }


def test_summary_wird_als_kommentar_ohne_pfad_gefuehrt():
    entry = build_review_entry(record())
    assert entry["tool"] == "naudit"
    bodies = [c for c in entry["review_comments"] if c["path"] is None]
    assert len(bodies) == 1
    assert bodies[0]["body"] == "Zusammenfassung"
    assert bodies[0]["line"] is None


def test_inline_kommentare_behalten_pfad_und_zeile():
    entry = build_review_entry(record())
    inline = [c for c in entry["review_comments"] if c["path"] is not None]
    assert inline == [{"path": "a.cs", "line": 5, "body": "Fund", "created_at": None}]


def test_merge_laesst_golden_comments_und_fremde_tools_unberuehrt():
    data = {
        "https://github.com/getsentry/sentry/pull/1": {
            "golden_comments": [{"comment": "echter Mangel", "severity": "High"}],
            "reviews": [{"tool": "coderabbit", "review_comments": []}],
        }
    }
    merged = merge(data, [record()], force=False)
    pr = merged["https://github.com/getsentry/sentry/pull/1"]
    assert pr["golden_comments"] == [{"comment": "echter Mangel", "severity": "High"}]
    assert [r["tool"] for r in pr["reviews"]] == ["coderabbit", "naudit"]


def test_merge_verweigert_doppelten_import_ohne_force():
    data = {
        "https://github.com/getsentry/sentry/pull/1": {
            "golden_comments": [],
            "reviews": [{"tool": "naudit", "review_comments": []}],
        }
    }
    with pytest.raises(SystemExit):
        merge(data, [record()], force=False)


@pytest.mark.parametrize("diagnostics", [
    {"checkoutRequested": True, "warnings": [], "error": "Checkout fehlgeschlagen"},
    {"checkoutRequested": False, "warnings": [], "error": None},
    {"checkoutRequested": True, "warnings": ["Warning: git fetch schlug fehl"], "error": None},
])
def test_merge_verweigert_import_bei_degradiertem_review(diagnostics):
    # Alle drei Fälle heißen: das Review lief nicht unter vollen Bedingungen. Importiert
    # zählte es als "nichts gefunden" und würde den Recall verfälschen.
    bad = record()
    bad["diagnostics"] = diagnostics
    data = {"https://github.com/getsentry/sentry/pull/1": {"golden_comments": [], "reviews": []}}
    with pytest.raises(SystemExit):
        merge(data, [bad], force=False)


def test_merge_meldet_unbekannte_url():
    data = {}
    with pytest.raises(SystemExit):
        merge(data, [record("https://github.com/unbekannt/repo/pull/9")], force=False)
