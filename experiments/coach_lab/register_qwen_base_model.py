from __future__ import annotations

import argparse
import json
from pathlib import Path

from coach_model_stack import (
    DEFAULT_BASE_MODEL_ID,
    DEFAULT_DB_PATH,
    ModelRecord,
    register_model,
    slugify,
    version_name,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Register a pretrained Qwen base judge for Coach Lab runtime inference."
    )
    parser.add_argument("--db", type=Path, default=DEFAULT_DB_PATH)
    parser.add_argument("--model-id", default=DEFAULT_BASE_MODEL_ID)
    parser.add_argument("--display-name", default="")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    model_version = version_name(f"qwen-base-judge-{slugify(args.model_id)}")
    display_name = args.display_name or f"Qwen Base Judge ({args.model_id})"
    metadata = {
        "model_kind": "qwen_base",
        "hf_model_id": args.model_id,
        "adapter_dir": "",
        "prompt_version": "coach-judge-v1",
        "status": "registered-pretrained",
    }
    register_model(
        args.db,
        ModelRecord(
            model_version=model_version,
            model_kind="qwen_base",
            display_name=display_name,
            provider="huggingface-transformers",
            metadata=metadata,
        ),
    )
    print(
        json.dumps(
            {
                "ModelVersion": model_version,
                "ModelKind": "qwen_base",
                "ModelId": args.model_id,
                "Registered": True,
            },
            ensure_ascii=True,
        )
    )


if __name__ == "__main__":
    main()
