from __future__ import annotations

import argparse
import json
from pathlib import Path

from coach_model_stack import (
    DEFAULT_DB_PATH,
    DEFAULT_TEACHER_MODEL_ID,
    ModelRecord,
    register_model,
    slugify,
    version_name,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Register an active Qwen teacher model for Coach Lab draft labeling."
    )
    parser.add_argument("--db", type=Path, default=DEFAULT_DB_PATH)
    parser.add_argument("--model-id", default=DEFAULT_TEACHER_MODEL_ID)
    parser.add_argument("--display-name", default="")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    model_version = version_name(f"qwen-teacher-{slugify(args.model_id)}")
    display_name = args.display_name or f"Qwen Teacher ({args.model_id})"
    metadata = {
        "model_kind": "qwen_teacher",
        "hf_model_id": args.model_id,
        "prompt_version": "coach-teacher-v1",
    }
    register_model(
        args.db,
        ModelRecord(
            model_version=model_version,
            model_kind="qwen_teacher",
            display_name=display_name,
            provider="huggingface-transformers",
            metadata=metadata,
        ),
    )
    print(
        json.dumps(
            {
                "ModelVersion": model_version,
                "ModelKind": "qwen_teacher",
                "ModelId": args.model_id,
                "Registered": True,
            },
            ensure_ascii=True,
        )
    )


if __name__ == "__main__":
    main()
