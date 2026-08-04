#!/usr/bin/env python3
"""Trägt Naudits aufgefangene Reviews als Tool `naudit` in die benchmark_data.json ein.

Bewusst konservativ: golden_comments und fremde Tool-Einträge werden gelesen und
unverändert zurückgeschrieben. Ein Review mit Fehlerdiagnose wird nicht importiert —
sonst zählte ein fehlgeschlagener Lauf als "nichts gefunden".
"""

import argparse
import json
import os
import sys


def build_review_entry(record: dict) -> dict:
    """Baut den Review-Eintrag im Schema, das step1_download_prs.py erzeugt."""
    review = record["review"]
    comments = [
        # Summary: wie ein Top-Level-Review-Body — ohne Pfad und Zeile.
        {"path": None, "line": None, "body": review["summary"], "created_at": None}
    ]
    comments += [
        {"path": c["filePath"], "line": c["newLine"], "body": c["body"], "created_at": None}
        for c in review["comments"]
    ]
    return {
        "tool": "naudit",
        "repo_name": review["projectId"],
        "pr_url": record["url"],
        "review_comments": comments,
    }


def merge(data: dict, records: list[dict], force: bool) -> dict:
    for record in records:
        diag = record.get("diagnostics") or {}
        reason = None
        if diag.get("error"):
            reason = f"Fehler: {diag['error']}"
        elif not diag.get("checkoutRequested", False):
            reason = "kein Checkout angefragt — Review lief ohne Repo-Kontext"
        elif diag.get("warnings"):
            reason = "Warnungen der Pipeline: " + " | ".join(diag["warnings"])
        if reason:
            # Naudit ist fail-open: ein degradiertes Review sieht im Ergebnis nur schwächer aus.
            # Importiert verfälschte es den Recall — also wiederholen statt übernehmen.
            sys.exit(f"Abbruch: {record['url']} lief nicht unter vollen Bedingungen ({reason}).")

        url = record["url"]
        if url not in data:
            sys.exit(f"Abbruch: {url} kommt in benchmark_data.json nicht vor.")

        reviews = data[url].setdefault("reviews", [])
        if any(r.get("tool") == "naudit" for r in reviews):
            if not force:
                sys.exit(f"Abbruch: für {url} existiert bereits ein naudit-Eintrag (--force zum Ersetzen).")
            reviews[:] = [r for r in reviews if r.get("tool") != "naudit"]

        reviews.append(build_review_entry(record))
    return data


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reviews", required=True, help="naudit-reviews.json aus dem Runner")
    parser.add_argument("--benchmark-data", required=True, help="results/benchmark_data.json")
    parser.add_argument("--force", action="store_true", help="vorhandene naudit-Einträge ersetzen")
    args = parser.parse_args()

    with open(args.reviews, encoding="utf-8") as f:
        records = json.load(f)
    with open(args.benchmark_data, encoding="utf-8") as f:
        data = json.load(f)

    before = sum(len(e.get("reviews", [])) for e in data.values())
    merged = merge(data, records, args.force)
    after = sum(len(e.get("reviews", [])) for e in merged.values())

    # Atomares Schreiben: in Temp-Datei schreiben, dann ersetzen.
    # So bleibt die Original unverändert, falls der Prozess abbricht.
    tmp_path = f"{args.benchmark_data}.tmp"
    try:
        with open(tmp_path, "w", encoding="utf-8") as f:
            json.dump(merged, f, indent=2)
        os.replace(tmp_path, args.benchmark_data)
    except Exception:
        # Im Fehlerfall: Temp-Datei löschen, Original unangetastet.
        if os.path.exists(tmp_path):
            os.remove(tmp_path)
        raise

    print(f"{len(records)} Reviews importiert. Review-Einträge gesamt: {before} → {after}")


if __name__ == "__main__":
    main()
