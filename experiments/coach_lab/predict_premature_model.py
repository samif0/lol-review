from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image


OBJECTIVE_KEYS = {
    "favorable_trade_windows",
    "respect_jungle_support_threat",
    "safe_lane_spacing",
    "recall_on_crash_and_tempo",
    "punish_enemy_cooldown_windows",
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run a premature Coach Lab prototype model over storyboard + minimap images."
    )
    parser.add_argument("--model-dir", type=Path, required=True)
    parser.add_argument("--storyboard", type=Path, required=True)
    parser.add_argument("--minimap", type=Path, required=True)
    parser.add_argument("--game-time-s", type=int, default=0)
    return parser.parse_args()


def image_vector(path: Path, size: tuple[int, int]) -> np.ndarray:
    with Image.open(path) as image:
        rgb = image.convert("RGB").resize(size, Image.Resampling.BILINEAR)
        array = np.asarray(rgb, dtype=np.float32) / 255.0
    return array.reshape(-1)


def build_feature(storyboard_path: Path, minimap_path: Path, game_time_s: int, spec: dict[str, Any]) -> np.ndarray:
    storyboard_size = tuple(spec.get("storyboard_size", [48, 32]))
    minimap_size = tuple(spec.get("minimap_size", [48, 12]))
    storyboard = image_vector(storyboard_path, storyboard_size)
    minimap = image_vector(minimap_path, minimap_size)
    game_time = np.array([min(1.0, max(0, game_time_s) / 600.0)], dtype=np.float32)
    feature = np.concatenate([storyboard, minimap, game_time], dtype=np.float32)
    norm = np.linalg.norm(feature)
    if norm > 0:
        feature = feature / norm
    return feature


def predict_head(feature: np.ndarray, head: dict[str, Any]) -> tuple[str, float]:
    labels = head.get("labels", [])
    centroids = head.get("centroids", [])
    if not labels or not centroids:
        return "", 0.2

    matrix = np.asarray(centroids, dtype=np.float32)
    scores = matrix @ feature
    top_index = int(np.argmax(scores))
    top_score = float(scores[top_index])
    if len(scores) > 1:
        sorted_scores = np.sort(scores)
        margin = float(sorted_scores[-1] - sorted_scores[-2])
    else:
        margin = 0.1

    confidence = 0.35 + max(0.0, top_score) * 0.35 + max(0.0, margin) * 0.2
    confidence = float(max(0.2, min(0.82, confidence)))
    return str(labels[top_index]), confidence


def main() -> None:
    args = parse_args()
    model_path = args.model_dir / "model.json"
    payload = json.loads(model_path.read_text(encoding="utf-8"))
    feature = build_feature(
        args.storyboard,
        args.minimap,
        args.game_time_s,
        payload.get("FeatureSpec", {}),
    )

    quality, quality_conf = predict_head(feature, payload.get("QualityHead", {}))
    reason, reason_conf = predict_head(feature, payload.get("ReasonHead", {}))
    confidence = round((quality_conf + reason_conf) / 2.0, 3)
    objective_key = reason if reason in OBJECTIVE_KEYS else ""
    rationale = (
        f"Premature prototype prediction from storyboard + minimap. "
        f"Trained from {payload.get('TrainingExamples', 0)} accepted clips, so this is exploratory."
    )

    result = {
        "ModelVersion": payload.get("ModelVersion", "premature-prototype"),
        "MomentQuality": quality or "neutral",
        "PrimaryReason": reason or "manual_clip_review",
        "ObjectiveKey": objective_key,
        "Confidence": confidence,
        "Rationale": rationale,
    }
    print(json.dumps(result))


if __name__ == "__main__":
    main()
