"""
One-shot repair: apply classification CSV metadata onto ProcessCatalogEntries
in the live Heimdall SQLite DB (same semantics as ApplyImportMetadataAsync).
"""
from __future__ import annotations

import csv
import re
import sqlite3
from pathlib import Path

CSV_PATH = Path(r"c:\Users\christopher.owen\Downloads\HD Discovered classified.csv")
DB_PATH = Path(r"C:\ProgramData\Heimdall\heimdall.db")

GROUP_MAP = {
    "corewindows": 0,
    "core windows": 0,
    "core": 0,
    "soe": 1,
    "specialization": 2,
    "spec": 2,
}

DRIVERSTORE_RE = re.compile(r"\\DriverStore\\FileRepository\\[^\\]+\\", re.I)
WINDOWSAPPS_RE = re.compile(
    r"\\WindowsApps\\([^\\]+?)_[0-9][^\\]*?(_(?:x64|x86|arm|arm64|neutral)__[^\\]+\\)",
    re.I,
)
VERSION_FOLDER_RE = re.compile(r"\\(\d+(?:\.\d+){1,3}(?:\.\d+)?)\\")


def normalize_path(path: str | None) -> str:
    if not path or not path.strip():
        return ""
    p = path.strip().replace("/", "\\")
    p = DRIVERSTORE_RE.sub(r"\\DriverStore\\FileRepository\\{hash}\\", p)
    p = WINDOWSAPPS_RE.sub(lambda m: rf"\WindowsApps\{m.group(1)}{{version}}{m.group(2)}", p)
    p = VERSION_FOLDER_RE.sub(r"\\{version}\\", p)
    return p.lower()


def normalize_name(name: str) -> str:
    n = (name or "").strip()
    if n.lower().endswith(".exe"):
        n = n[:-4]
    return n.strip()


def parse_group(raw: str | None) -> int | None:
    if not raw or not raw.strip():
        return None
    return GROUP_MAP.get(raw.strip().lower())


def main() -> None:
    if not CSV_PATH.is_file():
        raise SystemExit(f"CSV not found: {CSV_PATH}")
    if not DB_PATH.is_file():
        raise SystemExit(f"DB not found: {DB_PATH}")

    conn = sqlite3.connect(str(DB_PATH), timeout=30)
    conn.row_factory = sqlite3.Row
    cur = conn.cursor()
    cur.execute("PRAGMA busy_timeout=30000")

    rows = list(cur.execute(
        "SELECT Id, ProcessName, ExecutablePath, DisplayName, Description, Category, Subcategory, SuggestedGroup, SuggestionReason FROM ProcessCatalogEntries"
    ))
    by_name: dict[str, list[sqlite3.Row]] = {}
    identity: dict[tuple[str, str], sqlite3.Row] = {}
    canonical: dict[tuple[str, str], sqlite3.Row] = {}
    for r in rows:
        key = r["ProcessName"].lower()
        by_name.setdefault(key, []).append(r)
        identity[(key, (r["ExecutablePath"] or "").strip().lower())] = r
        canon = normalize_path(r["ExecutablePath"])
        if canon:
            canonical[(key, canon)] = r

    updated = 0
    matched_rows = 0
    unmatched = 0

    with CSV_PATH.open(encoding="utf-8-sig", newline="") as f:
        reader = csv.DictReader(f)
        for csv_row in reader:
            pname = normalize_name(csv_row.get("ProcessName") or "")
            if not pname:
                continue
            existing = by_name.get(pname.lower())
            if not existing:
                unmatched += 1
                continue

            path = (csv_row.get("ExecutablePath") or "").strip()
            display = (csv_row.get("DisplayName") or "").strip() or None
            desc = (csv_row.get("Description") or "").strip() or None
            cat = (csv_row.get("Category") or "").strip() or None
            sub = (csv_row.get("Subcategory") or "").strip() or None
            group = parse_group(csv_row.get("Group"))

            if path:
                exact = identity.get((pname.lower(), path.lower()))
                if exact is not None:
                    targets = [exact]
                else:
                    canon = normalize_path(path)
                    by_canon = canonical.get((pname.lower(), canon)) if canon else None
                    if by_canon is not None:
                        targets = [by_canon]
                    else:
                        blank = identity.get((pname.lower(), ""))
                        targets = [blank] if blank is not None else existing
            else:
                targets = existing

            matched_rows += 1
            for entry in targets:
                changes = []
                params: list = []
                if display is not None and (entry["DisplayName"] or "") != display:
                    changes.append("DisplayName=?")
                    params.append(display)
                if desc is not None and (entry["Description"] or "") != desc:
                    changes.append("Description=?")
                    params.append(desc)
                if cat is not None and (entry["Category"] or "") != cat:
                    changes.append("Category=?")
                    params.append(cat)
                if sub is not None and (entry["Subcategory"] or "") != sub:
                    changes.append("Subcategory=?")
                    params.append(sub)
                if group is not None and entry["SuggestedGroup"] != group:
                    changes.append("SuggestedGroup=?")
                    params.append(group)
                    changes.append("SuggestionReason=?")
                    params.append("Imported classification CSV")
                if not changes:
                    continue
                params.append(entry["Id"])
                cur.execute(
                    f"UPDATE ProcessCatalogEntries SET {', '.join(changes)} WHERE Id=?",
                    params,
                )
                updated += 1

    conn.commit()
    counts = cur.execute(
        """
        SELECT COUNT(*),
          SUM(CASE WHEN Category IS NOT NULL AND Category != '' THEN 1 ELSE 0 END),
          SUM(CASE WHEN Subcategory IS NOT NULL AND Subcategory != '' THEN 1 ELSE 0 END),
          SUM(CASE WHEN Description IS NOT NULL AND Description != '' THEN 1 ELSE 0 END),
          SUM(CASE WHEN SuggestionReason = 'Imported classification CSV' THEN 1 ELSE 0 END)
        FROM ProcessCatalogEntries
        """
    ).fetchone()
    conn.close()
    print(f"CSV matched process names: {matched_rows}, no DB name: {unmatched}")
    print(f"Catalog rows updated: {updated}")
    print(f"DB totals (all, with_cat, with_sub, with_desc, imported_suggestions): {tuple(counts)}")


if __name__ == "__main__":
    main()
