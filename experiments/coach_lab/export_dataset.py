from __future__ import annotations

import argparse
import json
import os
import sqlite3
from collections.abc import Iterable
from pathlib import Path
from typing import Any


EXPECTED_TABLES = {
    "coach_moments",
    "coach_inferences",
    "coach_labels",
    "coach_dataset_versions",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Export hidden Coach Lab data into JSONL for later multimodal training."
    )
    parser.add_argument(
        "--db",
        type=Path,
        default=Path(os.environ["LOCALAPPDATA"]) / "LoLReviewData" / "lol_review.db",
        help="Path to lol_review.db",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path(os.environ["LOCALAPPDATA"]) / "LoLReviewData" / "coach-analysis" / "exports" / "bootstrap-v1",
        help="Output directory for JSONL exports.",
    )
    parser.add_argument(
        "--gold-only",
        action="store_true",
        help="Export only manually labeled gold moments.",
    )
    return parser.parse_args()


def ensure_expected_tables(conn: sqlite3.Connection) -> None:
    rows = conn.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'coach_%'"
    ).fetchall()
    table_names = {row[0] for row in rows}
    missing = sorted(EXPECTED_TABLES - table_names)
    if not missing:
        return

    missing_text = ", ".join(missing)
    raise SystemExit(
        "Coach tables are not initialized in the local database yet. "
        f"Missing: {missing_text}. "
        "Launch the updated app once so DatabaseInitializer creates the coach_* tables, "
        "then open Coach Lab and sync moments before exporting."
    )


def fetch_rows(conn: sqlite3.Connection) -> list[dict[str, Any]]:
    conn.row_factory = sqlite3.Row
    query = """
        SELECT
            m.id,
            m.player_id,
            m.game_id,
            m.bookmark_id,
            m.objective_block_id,
            m.source_type,
            m.patch_version,
            m.champion,
            m.role,
            m.game_time_s,
            m.clip_start_s,
            m.clip_end_s,
            m.clip_path,
            m.storyboard_path,
            m.hud_strip_path,
            m.minimap_strip_path,
            m.manifest_path,
            m.note_text,
            m.context_text,
            m.dataset_version,
            m.model_version,
            m.created_at,
            m.reviewed_at,
            COALESCE(i.moment_quality, '') AS draft_quality,
            COALESCE(i.primary_reason, '') AS draft_primary_reason,
            COALESCE(i.objective_key, '') AS draft_objective_key,
            i.attached_objective_id AS draft_attached_objective_id,
            COALESCE(i.attached_objective_title, '') AS draft_attached_objective_title,
            COALESCE(i.confidence, 0) AS draft_confidence,
            COALESCE(i.rationale, '') AS draft_rationale,
            COALESCE(i.model_version, '') AS draft_model_version,
            COALESCE(i.inference_mode, '') AS draft_inference_mode,
            COALESCE(l.label_quality, '') AS label_quality,
            COALESCE(l.primary_reason, '') AS label_primary_reason,
            COALESCE(l.objective_key, '') AS label_objective_key,
            l.attached_objective_id AS label_attached_objective_id,
            COALESCE(l.attached_objective_title, '') AS label_attached_objective_title,
            COALESCE(l.explanation, '') AS label_explanation,
            COALESCE(l.confidence, 0) AS label_confidence,
            COALESCE(l.source, '') AS label_source,
            b.objective_title,
            b.objective_key AS block_objective_key,
            g.review_notes,
            g.mistakes,
            g.focus_next,
            g.went_well,
            g.spotted_problems
        FROM coach_moments m
        LEFT JOIN coach_inferences i ON i.moment_id = m.id
        LEFT JOIN coach_labels l ON l.moment_id = m.id
        LEFT JOIN coach_objective_blocks b ON b.id = m.objective_block_id
        LEFT JOIN games g ON g.game_id = m.game_id
        ORDER BY m.created_at ASC, m.id ASC
    """
    return [dict(row) for row in conn.execute(query).fetchall()]


def infer_bucket(row: dict[str, Any]) -> str:
    if row["label_quality"]:
        return "gold"
    if row["draft_quality"]:
        return "silver" if row["source_type"] == "manual_clip" else "bronze"
    return "bronze"


def build_record(row: dict[str, Any]) -> dict[str, Any]:
    bucket = infer_bucket(row)
    manual_label = None
    if row["label_quality"]:
        manual_label = {
            "quality": row["label_quality"],
            "primary_reason": row["label_primary_reason"],
            "objective_key": row["label_objective_key"],
            "attached_objective_id": row["label_attached_objective_id"],
            "attached_objective_title": row["label_attached_objective_title"],
            "explanation": row["label_explanation"],
            "confidence": row["label_confidence"],
            "source": row["label_source"] or "manual",
        }

    draft_label = None
    if row["draft_quality"]:
        draft_label = {
            "quality": row["draft_quality"],
            "primary_reason": row["draft_primary_reason"],
            "objective_key": row["draft_objective_key"],
            "attached_objective_id": row["draft_attached_objective_id"],
            "attached_objective_title": row["draft_attached_objective_title"],
            "confidence": row["draft_confidence"],
            "rationale": row["draft_rationale"],
            "model_version": row["draft_model_version"],
            "inference_mode": row["draft_inference_mode"],
        }

    return {
        "moment_id": row["id"],
        "player_id": row["player_id"],
        "game_id": row["game_id"],
        "bookmark_id": row["bookmark_id"],
        "objective_block_id": row["objective_block_id"],
        "supervision_bucket": bucket,
        "source_type": row["source_type"],
        "training_input": {
            "champion": row["champion"],
            "role": row["role"],
            "patch_version": row["patch_version"],
            "game_time_s": row["game_time_s"],
            "clip_start_s": row["clip_start_s"],
            "clip_end_s": row["clip_end_s"],
            "dataset_version": row["dataset_version"],
            "active_model_version": row["model_version"],
            "active_objective_title": row["objective_title"] or "",
            "active_objective_key": row["block_objective_key"] or "",
            # Keep the primary runtime payload note-blind. Notes stay in supervision metadata.
            "context_text": row["context_text"] or "",
            "artifacts": {
                "clip_path": row["clip_path"],
                "storyboard_path": row["storyboard_path"],
                "minimap_strip_path": row["minimap_strip_path"],
                "manifest_path": row["manifest_path"],
            },
        },
        "supervision": {
            "clip_note": row["note_text"] or "",
            "manual_label": manual_label,
            "draft_label": draft_label,
            "review_fields": {
                "review_notes": row["review_notes"] or "",
                "mistakes": row["mistakes"] or "",
                "focus_next": row["focus_next"] or "",
                "went_well": row["went_well"] or "",
                "spotted_problems": row["spotted_problems"] or "",
            },
        },
        "timestamps": {
            "created_at": row["created_at"],
            "reviewed_at": row["reviewed_at"],
        },
    }


def write_jsonl(path: Path, rows: Iterable[dict[str, Any]]) -> int:
    path.parent.mkdir(parents=True, exist_ok=True)
    count = 0
    with path.open("w", encoding="utf-8") as handle:
        for row in rows:
            handle.write(json.dumps(row, ensure_ascii=True) + "\n")
            count += 1
    return count


def write_summary(path: Path, records: list[dict[str, Any]]) -> dict[str, Any]:
    summary = {
        "total_moments": len(records),
        "gold_moments": sum(1 for record in records if record["supervision_bucket"] == "gold"),
        "silver_moments": sum(1 for record in records if record["supervision_bucket"] == "silver"),
        "bronze_moments": sum(1 for record in records if record["supervision_bucket"] == "bronze"),
        "manual_clip_moments": sum(1 for record in records if record["source_type"] == "manual_clip"),
        "auto_sample_moments": sum(1 for record in records if record["source_type"] == "auto_sample"),
        "games_covered": len({record["game_id"] for record in records}),
        "players_covered": len({record["player_id"] for record in records}),
    }
    path.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    return summary


def main() -> None:
    args = parse_args()
    if not args.db.exists():
        raise SystemExit(f"Database not found: {args.db}")

    args.output.mkdir(parents=True, exist_ok=True)

    conn = sqlite3.connect(args.db)
    try:
        ensure_expected_tables(conn)
        raw_rows = fetch_rows(conn)
    finally:
        conn.close()

    records = [build_record(row) for row in raw_rows]
    gold = [record for record in records if record["supervision_bucket"] == "gold"]
    silver = [record for record in records if record["supervision_bucket"] == "silver"]
    bronze = [record for record in records if record["supervision_bucket"] == "bronze"]

    if args.gold_only:
        count = write_jsonl(args.output / "moments.gold.jsonl", gold)
        summary = write_summary(args.output / "summary.gold.json", gold)
        print(json.dumps({"written": count, "summary": summary, "output": str(args.output)}, indent=2))
        return

    total_count = write_jsonl(args.output / "moments.jsonl", records)
    gold_count = write_jsonl(args.output / "moments.gold.jsonl", gold)
    silver_count = write_jsonl(args.output / "moments.silver.jsonl", silver)
    bronze_count = write_jsonl(args.output / "moments.bronze.jsonl", bronze)
    summary = write_summary(args.output / "summary.json", records)

    print(
        json.dumps(
            {
                "written": {
                    "moments": total_count,
                    "gold": gold_count,
                    "silver": silver_count,
                    "bronze": bronze_count,
                },
                "summary": summary,
                "output_dir": str(args.output),
            },
            indent=2,
        )
    )


if __name__ == "__main__":
    main()
