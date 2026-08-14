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


def degradation_reason(diag: dict) -> str | None:
    """Lief das Review NICHT unter vollen Bedingungen? Dann der Grund, sonst None.

    Spiegelt ReviewAnomalies.Of im Runner. Fehlende Felder (Ergebnisdatei aus einem älteren
    Lauf) gelten bewusst als "nicht nachgewiesen" und damit als Ablehnungsgrund — die sichere
    Richtung: lieber ein Review erneut fahren als ein degradiertes in die Zahl lassen.
    """
    if diag.get("error"):
        return f"Fehler: {diag['error']}"
    if not diag.get("checkoutRequested", False):
        return "kein Checkout angefragt — Review lief ohne Repo-Kontext"
    if diag.get("checkoutFailed", False):
        return "Checkout fehlgeschlagen — Review lief diff-only, ohne Repo-Kontext und ohne frisches Profil"
    if not diag.get("contextInPrompt", False):
        return "kein Repo-Kontext im Prompt — die Kontextsammlung kam leer zurück"
    if not diag.get("guidelinesInPrompt", False):
        return "kein Architektur-Profil im Prompt — Destillation leer oder gescheitert"
    if diag.get("warnings"):
        return "Warnungen der Pipeline: " + " | ".join(diag["warnings"])
    return None


def check_complete(data: dict, records: list[dict], allow_partial: bool) -> None:
    """Bricht ab, solange nicht jeder Schlüssel der Zieldatei einen Datensatz hat.

    Der Benchmark rechnet Recall je Tool über ALLE PRs der Zieldatei. Werden 30 statt 50
    importiert, rechnet die Auswertung Naudit nur über die importierten, die 41 Vergleichstools
    aber über alle 50 — und die fehlenden wären ausgerechnet die schweren. Naudit sähe damit
    besser aus, als es ist. Übersteuerbar mit --allow-partial, dann aber laut.
    """
    vorhanden = {record["url"] for record in records}
    fehlend = [url for url in data if url not in vorhanden]
    if not fehlend:
        return
    if not allow_partial:
        sys.exit(
            f"Abbruch: unvollständiger Lauf — {len(fehlend)} von {len(data)} PRs der Zieldatei "
            f"haben keinen Datensatz. Der Benchmark rechnet Recall über alle PRs; ein Teilimport "
            f"macht Naudit besser, als es ist. Erst zu Ende laufen lassen (oder --allow-partial "
            f"setzen und wissen, was die Zahl dann bedeutet).\nFehlend: "
            + "\n  ".join([""] + fehlend))
    print(
        f"WARNUNG: Teilimport — nur {len(data) - len(fehlend)} von {len(data)} PRs der Zieldatei "
        f"haben einen Datensatz. Precision/Recall sind mit den 41 Vergleichstools NICHT "
        f"vergleichbar: die rechnen über alle {len(data)} PRs, Naudit nur über die importierten.",
        file=sys.stderr)


def merge(data: dict, records: list[dict], force: bool, allow_partial: bool = False) -> dict:
    check_complete(data, records, allow_partial)

    for record in records:
        reason = degradation_reason(record.get("diagnostics") or {})
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
    parser.add_argument("--allow-partial", action="store_true",
                        help="unvollständigen Lauf importieren — die Zahl ist dann NICHT mit den "
                             "übrigen Tools vergleichbar")
    args = parser.parse_args()

    with open(args.reviews, encoding="utf-8") as f:
        records = json.load(f)
    with open(args.benchmark_data, encoding="utf-8") as f:
        data = json.load(f)

    before = sum(len(e.get("reviews", [])) for e in data.values())
    merged = merge(data, records, args.force, args.allow_partial)
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
