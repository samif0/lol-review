from __future__ import annotations

import argparse
import json

from coach_model_stack import (
    judge_prompt,
    load_qwen_model,
    normalize_prediction,
    run_qwen_generation,
    teacher_prompt,
    extract_json_object,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run a Qwen VL judge or teacher draft over Coach Lab storyboard + minimap images."
    )
    parser.add_argument("--storyboard", required=True)
    parser.add_argument("--minimap", required=True)
    parser.add_argument("--game-time-s", type=int, default=0)
    parser.add_argument("--champion", default="Unknown")
    parser.add_argument("--role", default="adc")
    parser.add_argument("--active-objective-title", default="")
    parser.add_argument("--note-text", default="")
    parser.add_argument("--review-context", default="")
    parser.add_argument("--source-type", default="manual_clip")
    parser.add_argument("--model-id", required=True)
    parser.add_argument("--model-version", required=True)
    parser.add_argument("--adapter-dir", default="")
    parser.add_argument("--mode", choices=("judge", "teacher"), default="judge")
    parser.add_argument("--max-new-tokens", type=int, default=256)
    return parser.parse_args()


def main() -> None:
    args = parse_args()

    record = {
        "training_input": {
            "champion": args.champion,
            "role": args.role,
            "game_time_s": args.game_time_s,
            "active_objective_title": args.active_objective_title,
            "artifacts": {
                "storyboard_path": args.storyboard,
                "minimap_strip_path": args.minimap,
            },
        },
        "supervision": {
            "clip_note": args.note_text,
            "review_fields": {
                "review_notes": args.review_context,
            },
        },
        "source_type": args.source_type,
    }

    prompt = teacher_prompt(record) if args.mode == "teacher" else judge_prompt(record)
    model, processor = load_qwen_model(model_id=args.model_id, adapter_dir=args.adapter_dir)
    raw_output = run_qwen_generation(
        model=model,
        processor=processor,
        image_paths=[args.storyboard, args.minimap],
        prompt=prompt,
        max_new_tokens=args.max_new_tokens,
    )
    parsed = extract_json_object(raw_output)
    normalized = normalize_prediction(parsed, model_version=args.model_version, mode=args.mode)

    print(
        json.dumps(
            {
                "ModelVersion": normalized["ModelVersion"],
                "MomentQuality": normalized["MomentQuality"],
                "PrimaryReason": normalized["PrimaryReason"],
                "ObjectiveKey": normalized["ObjectiveKey"],
                "Confidence": normalized["Confidence"],
                "Rationale": normalized["Rationale"],
            },
            ensure_ascii=True,
        )
    )


if __name__ == "__main__":
    main()
