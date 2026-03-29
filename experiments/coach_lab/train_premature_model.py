from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image


STORYBOARD_SIZE = (48, 32)
MINIMAP_SIZE = (48, 12)
OBJECTIVE_KEYS = {
    "favorable_trade_windows",
    "respect_jungle_support_threat",
    "safe_lane_spacing",
    "recall_on_crash_and_tempo",
    "punish_enemy_cooldown_windows",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Train a deliberately premature clip prototype from Coach Lab moments."
    )
    parser.add_argument("--input", type=Path, required=True, help="Path to moments.jsonl export.")
    parser.add_argument("--output", type=Path, required=True, help="Directory where prototype models are stored.")
    parser.add_argument("--min-examples", type=int, default=2, help="Minimum accepted examples required to train.")
    return parser.parse_args()


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                records.append(json.loads(line))
    return records


def load_feature(record: dict[str, Any]) -> np.ndarray | None:
    artifacts = record.get("training_input", {}).get("artifacts", {})
    storyboard_path = Path(artifacts.get("storyboard_path", ""))
    minimap_path = Path(artifacts.get("minimap_strip_path", ""))
    if not storyboard_path.exists() or not minimap_path.exists():
        return None

    storyboard = image_vector(storyboard_path, STORYBOARD_SIZE)
    minimap = image_vector(minimap_path, MINIMAP_SIZE)
    game_time_s = float(record.get("training_input", {}).get("game_time_s", 0) or 0)
    game_time = np.array([min(1.0, game_time_s / 600.0)], dtype=np.float32)

    feature = np.concatenate([storyboard, minimap, game_time], dtype=np.float32)
    norm = np.linalg.norm(feature)
    if norm > 0:
        feature = feature / norm
    return feature


def image_vector(path: Path, size: tuple[int, int]) -> np.ndarray:
    with Image.open(path) as image:
        rgb = image.convert("RGB").resize(size, Image.Resampling.BILINEAR)
        array = np.asarray(rgb, dtype=np.float32) / 255.0
    return array.reshape(-1)


def select_training_examples(records: list[dict[str, Any]]) -> list[dict[str, Any]]:
    examples: list[dict[str, Any]] = []
    for record in records:
        if record.get("source_type") != "manual_clip":
            continue

        supervision = record.get("supervision", {})
        manual = supervision.get("manual_label")
        draft = supervision.get("draft_label")
        label = manual or draft
        if not label:
            continue

        feature = load_feature(record)
        if feature is None:
            continue

        examples.append(
            {
                "feature": feature,
                "quality": label.get("quality", "neutral") or "neutral",
                "reason": label.get("primary_reason", "") or "",
                "bucket": "gold" if manual else "silver",
            }
        )
    return examples


def build_head(examples: list[dict[str, Any]], key: str) -> dict[str, Any]:
    grouped: dict[str, list[np.ndarray]] = {}
    for example in examples:
        label = str(example.get(key, "") or "").strip()
        if not label:
            continue
        grouped.setdefault(label, []).append(example["feature"])

    labels = sorted(grouped)
    if not labels:
        return {"labels": [], "centroids": [], "counts": {}}

    centroids: list[list[float]] = []
    counts: dict[str, int] = {}
    for label in labels:
        matrix = np.stack(grouped[label], axis=0)
        centroid = matrix.mean(axis=0)
        norm = np.linalg.norm(centroid)
        if norm > 0:
            centroid = centroid / norm
        centroids.append(centroid.astype(np.float32).tolist())
        counts[label] = int(matrix.shape[0])

    return {"labels": labels, "centroids": centroids, "counts": counts}


def main() -> None:
    args = parse_args()
    records = read_jsonl(args.input)
    examples = select_training_examples(records)

    if len(examples) < args.min_examples:
        raise SystemExit(
            f"Need at least {args.min_examples} accepted manual clips to train a premature prototype. "
            f"Found {len(examples)}."
        )

    quality_head = build_head(examples, "quality")
    reason_head = build_head(examples, "reason")
    gold_examples = sum(1 for example in examples if example["bucket"] == "gold")
    silver_examples = sum(1 for example in examples if example["bucket"] == "silver")
    timestamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    model_version = f"premature-prototype-{timestamp}"
    model_dir = args.output / model_version
    model_dir.mkdir(parents=True, exist_ok=True)

    payload = {
        "ModelVersion": model_version,
        "TrainingExamples": len(examples),
        "GoldExamples": gold_examples,
        "SilverExamples": silver_examples,
        "FeatureSpec": {
            "storyboard_size": STORYBOARD_SIZE,
            "minimap_size": MINIMAP_SIZE,
            "includes_game_time": True,
        },
        "QualityHead": quality_head,
        "ReasonHead": reason_head,
        "ObjectiveKeys": sorted(OBJECTIVE_KEYS),
    }

    (model_dir / "model.json").write_text(json.dumps(payload, indent=2), encoding="utf-8")

    result = {
        "ModelVersion": model_version,
        "TrainingExamples": len(examples),
        "GoldExamples": gold_examples,
        "SilverExamples": silver_examples,
        "ModelDirectory": str(model_dir),
    }
    print(json.dumps(result))


if __name__ == "__main__":
    main()
