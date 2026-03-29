from __future__ import annotations

import argparse
import json
from pathlib import Path

from coach_model_stack import (
    DEFAULT_EXPORT,
    DEFAULT_TEACHER_MODEL_ID,
    extract_json_object,
    load_qwen_model,
    normalize_prediction,
    read_jsonl,
    run_qwen_generation,
    teacher_prompt,
    write_jsonl,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run the Qwen teacher model over exported Coach Lab moments to generate offline draft labels."
    )
    parser.add_argument("--input", type=Path, default=DEFAULT_EXPORT / "moments.jsonl")
    parser.add_argument(
        "--output",
        type=Path,
        default=DEFAULT_EXPORT / "teacher-drafts.jsonl",
    )
    parser.add_argument("--model-id", default=DEFAULT_TEACHER_MODEL_ID)
    parser.add_argument("--model-version", default="")
    parser.add_argument("--max-samples", type=int, default=0)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    records = read_jsonl(args.input)
    candidate_records = [
        record
        for record in records
        if record.get("source_type") == "manual_clip"
        and Path(record.get("training_input", {}).get("artifacts", {}).get("storyboard_path", "")).exists()
        and Path(record.get("training_input", {}).get("artifacts", {}).get("minimap_strip_path", "")).exists()
    ]
    if args.max_samples > 0:
        candidate_records = candidate_records[: args.max_samples]

    if not candidate_records:
        raise SystemExit("No eligible manual clip moments were found in the export.")

    model_version = args.model_version or f"teacher::{args.model_id}"
    model, processor = load_qwen_model(model_id=args.model_id)

    rows: list[dict[str, object]] = []
    for record in candidate_records:
        artifacts = record.get("training_input", {}).get("artifacts", {})
        raw_output = run_qwen_generation(
            model=model,
            processor=processor,
            image_paths=[
                artifacts["storyboard_path"],
                artifacts["minimap_strip_path"],
            ],
            prompt=teacher_prompt(record),
            max_new_tokens=256,
        )
        parsed = extract_json_object(raw_output)
        normalized = normalize_prediction(parsed, model_version=model_version, mode="teacher")
        rows.append(
            {
                "moment_id": record.get("moment_id"),
                "game_id": record.get("game_id"),
                "model_version": normalized["ModelVersion"],
                "moment_quality": normalized["MomentQuality"],
                "primary_reason": normalized["PrimaryReason"],
                "objective_key": normalized["ObjectiveKey"],
                "confidence": normalized["Confidence"],
                "rationale": normalized["Rationale"],
                "raw_output": raw_output,
            }
        )

    count = write_jsonl(args.output, rows)
    print(
        json.dumps(
            {
                "written": count,
                "output": str(args.output),
                "model_version": model_version,
            },
            ensure_ascii=True,
        )
    )


if __name__ == "__main__":
    main()
