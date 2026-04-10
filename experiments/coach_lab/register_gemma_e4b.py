from __future__ import annotations

import argparse
import json
from pathlib import Path

from gemma_stack import (
    DEFAULT_BASE_MODEL_ID,
    DEFAULT_DB_PATH,
    DEFAULT_SYSTEM_PROMPT,
    ModelRecord,
    PROMPT_VERSION,
    register_model,
    slugify,
    version_name,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Register Gemma 4 E4B as the Coach Lab base inference model."
    )
    parser.add_argument("--db", type=Path, default=DEFAULT_DB_PATH)
    parser.add_argument("--model-id", default=DEFAULT_BASE_MODEL_ID)
    parser.add_argument("--display-name", default="")
    parser.add_argument("--activate", action="store_true")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    model_version = version_name(f"gemma-e4b-base-{slugify(args.model_id)}")
    display_name = args.display_name or f"Gemma 4 E4B ({args.model_id})"
    metadata = {
        "family": "gemma4",
        "role": "coach_base",
        "hf_model_id": args.model_id,
        "adapter_dir": "",
        "prompt_version": PROMPT_VERSION,
        "system_prompt": DEFAULT_SYSTEM_PROMPT,
        "status": "registered-pretrained",
    }
    register_model(
        args.db,
        ModelRecord(
            model_version=model_version,
            model_kind="gemma_base",
            display_name=display_name,
            provider="huggingface-transformers",
            metadata=metadata,
            is_active=True,
        ),
    )

    print(
        json.dumps(
            {
                "ModelVersion": model_version,
                "ModelKind": "gemma_base",
                "ModelId": args.model_id,
                "Registered": True,
                "Activated": True,
            },
            ensure_ascii=True,
        )
    )


if __name__ == "__main__":
    main()
