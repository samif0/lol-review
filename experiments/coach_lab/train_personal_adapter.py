from __future__ import annotations

import argparse
import json
import shutil
import subprocess
from pathlib import Path

from coach_model_stack import (
    DEFAULT_BASE_MODEL_ID,
    DEFAULT_DB_PATH,
    DEFAULT_EXPORT,
    MODELS_ROOT,
    ModelRecord,
    axolotl_available,
    ensure_dir,
    iter_clip_training_records,
    read_jsonl,
    register_model,
    version_name,
    write_axolotl_config,
    write_axolotl_dataset,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Prepare or train the personal Qwen adapter for Coach Lab."
    )
    parser.add_argument("--input", type=Path, default=DEFAULT_EXPORT / "moments.jsonl")
    parser.add_argument("--output", type=Path, default=MODELS_ROOT / "personal-adapter")
    parser.add_argument("--db", type=Path, default=DEFAULT_DB_PATH)
    parser.add_argument("--model-id", default=DEFAULT_BASE_MODEL_ID)
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--register", action="store_true")
    parser.add_argument("--epochs", type=float, default=3.0)
    parser.add_argument("--learning-rate", type=float, default=1.5e-4)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    records = read_jsonl(args.input)
    training_records = iter_clip_training_records(records, gold_only=True, include_silver=False)
    if len(training_records) < 2:
        raise SystemExit("Need at least 2 gold manual clips before preparing the personal adapter.")

    model_version = version_name("personal-adapter")
    model_root = ensure_dir(args.output / model_version)
    dataset_info = write_axolotl_dataset(training_records, model_root / "dataset", prompt_kind="judge")
    config_path = model_root / "axolotl.personal.yml"
    output_dir = ensure_dir(model_root / "output")
    write_axolotl_config(
        config_path,
        base_model_id=args.model_id,
        dataset_train_path=dataset_info["train_path"],
        dataset_eval_path=dataset_info["eval_path"],
        output_dir=str(output_dir),
        adapter_name=model_version,
        num_epochs=args.epochs,
        learning_rate=args.learning_rate,
    )

    trained = False
    train_reason = ""
    adapter_dir = ""
    if args.prepare_only:
        train_reason = "prepare_only_requested"
    elif not axolotl_available():
        train_reason = "axolotl_not_available_or_not_supported_on_this_os"
    elif shutil.which("axolotl") is None:
        train_reason = "axolotl_cli_not_found"
    else:
        subprocess.run(
            ["axolotl", "train", str(config_path)],
            check=True,
            cwd=str(config_path.parent),
        )
        adapter_dir = str(output_dir)
        trained = True
        train_reason = "trained"

    metadata = {
        "model_kind": "personal_adapter",
        "hf_model_id": args.model_id,
        "adapter_dir": adapter_dir,
        "dataset_dir": str(model_root / "dataset"),
        "config_path": str(config_path),
        "output_dir": str(output_dir),
        "prompt_version": "coach-judge-v1",
        "status": "trained" if trained else "prepared",
        "train_count": dataset_info["train_count"],
        "eval_count": dataset_info["eval_count"],
        "training_reason": train_reason,
    }
    (model_root / "metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")

    registered = False
    if trained and args.register:
        register_model(
            args.db,
            ModelRecord(
                model_version=model_version,
                model_kind="personal_adapter",
                display_name=f"Personal Adapter ({dataset_info['train_count']} gold clips)",
                provider="axolotl" if trained else "prepared",
                metadata=metadata,
                is_active=True,
            ),
        )
        registered = True

    print(
        json.dumps(
            {
                "ModelVersion": model_version,
                "ModelKind": "personal_adapter",
                "Prepared": True,
                "Trained": trained,
                "Registered": registered,
                "Reason": train_reason,
                "ModelDirectory": str(model_root),
                "AdapterDirectory": adapter_dir,
                "ConfigPath": str(config_path),
                "TrainCount": dataset_info["train_count"],
                "EvalCount": dataset_info["eval_count"],
            },
            ensure_ascii=True,
        )
    )


if __name__ == "__main__":
    main()
