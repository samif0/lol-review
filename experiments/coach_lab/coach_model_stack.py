from __future__ import annotations

import json
import os
import sqlite3
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable

from PIL import Image


LOCAL_APPDATA = Path(os.environ["LOCALAPPDATA"])
LOL_REVIEW_DATA = LOCAL_APPDATA / "LoLReviewData"
DEFAULT_DB_PATH = LOL_REVIEW_DATA / "lol_review.db"
COACH_ANALYSIS_ROOT = LOL_REVIEW_DATA / "coach-analysis"
EXPORT_ROOT = COACH_ANALYSIS_ROOT / "exports" / "bootstrap-v1"
DEFAULT_EXPORT = EXPORT_ROOT
MODELS_ROOT = COACH_ANALYSIS_ROOT / "models"

DEFAULT_TEACHER_MODEL_ID = "Qwen/Qwen2.5-VL-7B-Instruct"
DEFAULT_BASE_MODEL_ID = "Qwen/Qwen2.5-VL-3B-Instruct"

TEACHER_PROMPT_VERSION = "coach-teacher-v1"
JUDGE_PROMPT_VERSION = "coach-judge-v1"

OBJECTIVE_KEYS = {
    "favorable_trade_windows",
    "respect_jungle_support_threat",
    "safe_lane_spacing",
    "recall_on_crash_and_tempo",
    "punish_enemy_cooldown_windows",
}

REASON_KEYS = sorted(OBJECTIVE_KEYS | {"manual_clip_review", "lane_checkpoint"})


@dataclass(slots=True)
class ModelRecord:
    model_version: str
    model_kind: str
    display_name: str
    provider: str
    metadata: dict[str, Any]
    is_active: bool = True


def dependency_status() -> dict[str, bool]:
    import importlib.util

    packages = [
        "torch",
        "transformers",
        "accelerate",
        "peft",
        "qwen_vl_utils",
    ]
    return {package: importlib.util.find_spec(package) is not None for package in packages}


def require_packages(packages: Iterable[str], feature_name: str) -> None:
    status = dependency_status()
    missing = [package for package in packages if not status.get(package, False)]
    if not missing:
        return

    raise SystemExit(
        f"{feature_name} requires missing Python packages: {', '.join(missing)}. "
        "Install the Qwen stack first, then retry."
    )


def utc_timestamp() -> str:
    return datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")


def slugify(value: str) -> str:
    safe = []
    for char in value:
        if char.isalnum():
            safe.append(char.lower())
        elif char in {"/", "\\", " ", "-", ".", ":"}:
            safe.append("-")
    return "".join(safe).strip("-") or "model"


def version_name(prefix: str) -> str:
    return f"{prefix}-{utc_timestamp()}"


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return rows


def write_jsonl(path: Path, rows: Iterable[dict[str, Any]]) -> int:
    path.parent.mkdir(parents=True, exist_ok=True)
    count = 0
    with path.open("w", encoding="utf-8") as handle:
        for row in rows:
            handle.write(json.dumps(row, ensure_ascii=True) + "\n")
            count += 1
    return count


def ensure_dir(path: Path) -> Path:
    path.mkdir(parents=True, exist_ok=True)
    return path


def trim_text(value: str, max_length: int = 700) -> str:
    value = (value or "").strip()
    if len(value) <= max_length:
        return value
    return f"{value[: max_length - 3].rstrip()}..."


def extract_supervision(record: dict[str, Any]) -> dict[str, Any]:
    supervision = record.get("supervision", {})
    manual = supervision.get("manual_label")
    draft = supervision.get("draft_label")
    chosen = manual or draft or {}
    return {
        "quality": (chosen.get("quality") or "neutral").strip().lower(),
        "primary_reason": (chosen.get("primary_reason") or "").strip(),
        "objective_key": (chosen.get("objective_key") or "").strip(),
        "confidence": float(chosen.get("confidence") or 0.7),
        "rationale": trim_text(
            chosen.get("explanation")
            or chosen.get("rationale")
            or supervision.get("clip_note")
            or ""
        ),
        "bucket": "gold" if manual else "silver" if draft else "bronze",
    }


def iter_clip_training_records(
    records: list[dict[str, Any]],
    *,
    gold_only: bool = False,
    include_silver: bool = True,
) -> list[dict[str, Any]]:
    accepted: list[dict[str, Any]] = []
    for record in records:
        if record.get("source_type") != "manual_clip":
            continue

        supervision = extract_supervision(record)
        bucket = supervision["bucket"]
        if gold_only and bucket != "gold":
            continue
        if bucket == "silver" and not include_silver:
            continue
        if bucket == "bronze":
            continue

        artifacts = record.get("training_input", {}).get("artifacts", {})
        storyboard_path = Path(artifacts.get("storyboard_path", ""))
        minimap_path = Path(artifacts.get("minimap_strip_path", ""))
        if not storyboard_path.exists() or not minimap_path.exists():
            continue

        accepted.append(record)
    return accepted


def judge_prompt(record: dict[str, Any]) -> str:
    training_input = record.get("training_input", {})
    champion = training_input.get("champion") or "Unknown"
    role = training_input.get("role") or "adc"
    game_time_s = int(training_input.get("game_time_s") or 0)
    active_objective = training_input.get("active_objective_title") or ""

    prompt = f"""
You are a League of Legends lane coaching model.

You are given:
- a storyboard image from one short lane clip
- a minimap strip from the same clip
- metadata: champion={champion}, role={role}, game_time_s={game_time_s}
"""
    if active_objective:
        prompt += f'- current attached objective title: "{active_objective}"\n'

    prompt += """
Return strict JSON with these keys:
{
  "moment_quality": "good" | "bad" | "neutral",
  "primary_reason": one of [
    "favorable_trade_windows",
    "respect_jungle_support_threat",
    "safe_lane_spacing",
    "recall_on_crash_and_tempo",
    "punish_enemy_cooldown_windows",
    "manual_clip_review",
    "lane_checkpoint"
  ],
  "objective_key": same as primary_reason if it is one of the five coaching objectives, else "",
  "confidence": number from 0.0 to 1.0,
  "rationale": short one-sentence explanation
}

Use the images and metadata. Do not mention hidden chain-of-thought. Keep rationale short.
"""
    return prompt.strip()


def teacher_prompt(record: dict[str, Any]) -> str:
    training_input = record.get("training_input", {})
    supervision = record.get("supervision", {})
    champion = training_input.get("champion") or "Unknown"
    role = training_input.get("role") or "adc"
    game_time_s = int(training_input.get("game_time_s") or 0)
    clip_note = trim_text(supervision.get("clip_note") or "", 400)
    review_fields = supervision.get("review_fields", {})
    review_context = " ".join(
        value.strip()
        for value in [
            review_fields.get("review_notes") or "",
            review_fields.get("mistakes") or "",
            review_fields.get("focus_next") or "",
            review_fields.get("spotted_problems") or "",
        ]
        if value and value.strip()
    )
    review_context = trim_text(review_context, 500)
    active_objective = training_input.get("active_objective_title") or ""

    prompt = f"""
You are a draft-labeling teacher for a hidden League of Legends coaching tool.

You are given:
- a storyboard image from a short lane clip
- a minimap strip from the same clip
- metadata: champion={champion}, role={role}, game_time_s={game_time_s}
"""
    if active_objective:
        prompt += f'- current objective title: "{active_objective}"\n'
    if clip_note:
        prompt += f'- clip note: "{clip_note}"\n'
    if review_context:
        prompt += f'- review context: "{review_context}"\n'

    prompt += """
Return strict JSON with these keys:
{
  "moment_quality": "good" | "bad" | "neutral",
  "primary_reason": one of [
    "favorable_trade_windows",
    "respect_jungle_support_threat",
    "safe_lane_spacing",
    "recall_on_crash_and_tempo",
    "punish_enemy_cooldown_windows",
    "manual_clip_review",
    "lane_checkpoint"
  ],
  "objective_key": same as primary_reason if it is one of the five coaching objectives, else "",
  "confidence": number from 0.0 to 1.0,
  "rationale": short one-sentence explanation
}

You may use the clip note and review context as draft-labeling hints, but stay grounded in the images.
"""
    return prompt.strip()


def target_output(record: dict[str, Any]) -> str:
    chosen = extract_supervision(record)
    objective_key = chosen["objective_key"]
    primary_reason = chosen["primary_reason"] or "manual_clip_review"
    if not objective_key and primary_reason in OBJECTIVE_KEYS:
        objective_key = primary_reason

    payload = {
        "moment_quality": chosen["quality"] or "neutral",
        "primary_reason": primary_reason,
        "objective_key": objective_key,
        "confidence": round(float(chosen["confidence"] or 0.7), 2),
        "rationale": chosen["rationale"] or "Manual clip label.",
    }
    return json.dumps(payload, ensure_ascii=True)


def build_axolotl_sample(record: dict[str, Any], *, prompt_kind: str) -> dict[str, Any]:
    artifacts = record.get("training_input", {}).get("artifacts", {})
    storyboard_path = Path(artifacts.get("storyboard_path", "")).resolve()
    minimap_path = Path(artifacts.get("minimap_strip_path", "")).resolve()
    prompt = teacher_prompt(record) if prompt_kind == "teacher" else judge_prompt(record)
    output = target_output(record)

    return {
        "messages": [
            {
                "role": "user",
                "content": [
                    {"type": "image", "image": str(storyboard_path)},
                    {"type": "image", "image": str(minimap_path)},
                    {"type": "text", "text": prompt},
                ],
            },
            {
                "role": "assistant",
                "content": output,
            },
        ],
        "metadata": {
            "moment_id": record.get("moment_id"),
            "game_id": record.get("game_id"),
            "source_type": record.get("source_type"),
            "supervision_bucket": record.get("supervision_bucket"),
        },
    }


def split_train_eval(records: list[dict[str, Any]], eval_fraction: float = 0.15) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    if len(records) <= 2:
        return records, []

    by_game: dict[int, list[dict[str, Any]]] = {}
    for record in records:
        game_id = int(record.get("game_id") or 0)
        by_game.setdefault(game_id, []).append(record)

    game_ids = sorted(by_game)
    eval_games = max(1, int(round(len(game_ids) * eval_fraction)))
    eval_set = set(game_ids[-eval_games:])

    train_rows: list[dict[str, Any]] = []
    eval_rows: list[dict[str, Any]] = []
    for game_id in game_ids:
        target = eval_rows if game_id in eval_set else train_rows
        target.extend(by_game[game_id])

    if not train_rows:
        train_rows, eval_rows = records, []
    return train_rows, eval_rows


def write_axolotl_dataset(
    records: list[dict[str, Any]],
    output_dir: Path,
    *,
    prompt_kind: str,
) -> dict[str, Any]:
    output_dir = ensure_dir(output_dir)
    train_rows, eval_rows = split_train_eval(records)
    train_dataset = [build_axolotl_sample(record, prompt_kind=prompt_kind) for record in train_rows]
    eval_dataset = [build_axolotl_sample(record, prompt_kind=prompt_kind) for record in eval_rows]

    train_path = output_dir / "train.jsonl"
    eval_path = output_dir / "eval.jsonl"
    write_jsonl(train_path, train_dataset)
    if eval_dataset:
        write_jsonl(eval_path, eval_dataset)
    elif eval_path.exists():
        eval_path.unlink()

    return {
        "train_path": str(train_path),
        "eval_path": str(eval_path) if eval_dataset else "",
        "train_count": len(train_dataset),
        "eval_count": len(eval_dataset),
    }


def write_axolotl_config(
    path: Path,
    *,
    base_model_id: str,
    dataset_train_path: str,
    dataset_eval_path: str,
    output_dir: str,
    adapter_name: str,
    num_epochs: float,
    learning_rate: float,
) -> None:
    eval_section = ""
    if dataset_eval_path:
        eval_section = f"""
val_set_size: 0
test_datasets:
  - path: {dataset_eval_path}
    type: chat_template
"""

    content = f"""
base_model: {base_model_id}
processor_type: AutoProcessor
chat_template: qwen2_vl
skip_prepare_dataset: true
remove_unused_columns: false
sample_packing: false
adapter: qlora
load_in_4bit: true
bf16: auto
gradient_checkpointing: true
sequence_len: 4096
micro_batch_size: 1
gradient_accumulation_steps: 4
num_epochs: {num_epochs}
learning_rate: {learning_rate}
output_dir: {output_dir}
datasets:
  - path: {dataset_train_path}
    type: chat_template
dataset_prepared_path: {Path(output_dir).parent / "prepared"}
{eval_section}
lora_r: 16
lora_alpha: 32
lora_dropout: 0.05
lora_target_modules:
  - q_proj
  - k_proj
  - v_proj
  - o_proj
  - gate_proj
  - up_proj
  - down_proj
wandb_mode: disabled
special_tokens:
  pad_token: <|endoftext|>
"""
    path.write_text(content.strip() + "\n", encoding="utf-8")


def axolotl_available() -> bool:
    import importlib.util

    if os.name == "nt":
        return False

    return importlib.util.find_spec("axolotl") is not None


def register_model(db_path: Path, record: ModelRecord) -> None:
    if not db_path.exists():
        raise SystemExit(f"Database not found: {db_path}")

    conn = sqlite3.connect(db_path)
    try:
        now = int(datetime.now(timezone.utc).timestamp())
        conn.execute(
            """
            UPDATE coach_models
            SET is_active = 0
            WHERE model_kind = ?
            """,
            (record.model_kind,),
        )
        conn.execute(
            """
            INSERT INTO coach_models
                (model_version, model_kind, display_name, provider, is_active, metadata_json, created_at)
            VALUES
                (?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(model_version) DO UPDATE SET
                model_kind = excluded.model_kind,
                display_name = excluded.display_name,
                provider = excluded.provider,
                is_active = excluded.is_active,
                metadata_json = excluded.metadata_json
            """,
            (
                record.model_version,
                record.model_kind,
                record.display_name,
                record.provider,
                1 if record.is_active else 0,
                json.dumps(record.metadata, ensure_ascii=True),
                now,
            ),
        )
        conn.commit()
    finally:
        conn.close()


def extract_json_object(text: str) -> dict[str, Any]:
    text = (text or "").strip()
    if not text:
        raise ValueError("Model output is empty.")

    try:
        return json.loads(text)
    except json.JSONDecodeError:
        pass

    start = text.find("{")
    end = text.rfind("}")
    if start >= 0 and end > start:
        return json.loads(text[start : end + 1])

    raise ValueError("Could not find JSON object in model output.")


def normalize_prediction(payload: dict[str, Any], *, model_version: str, mode: str) -> dict[str, Any]:
    primary_reason = str(payload.get("primary_reason") or "").strip()
    objective_key = str(payload.get("objective_key") or "").strip()

    if not objective_key and primary_reason in OBJECTIVE_KEYS:
        objective_key = primary_reason

    return {
        "ModelVersion": model_version,
        "MomentQuality": str(payload.get("moment_quality") or "neutral").strip().lower() or "neutral",
        "PrimaryReason": primary_reason or "manual_clip_review",
        "ObjectiveKey": objective_key,
        "Confidence": max(0.0, min(1.0, float(payload.get("confidence") or 0.5))),
        "Rationale": trim_text(str(payload.get("rationale") or ""), 240),
        "Mode": mode,
    }


def choose_primary_inference_model(conn: sqlite3.Connection) -> dict[str, Any] | None:
    conn.row_factory = sqlite3.Row
    for model_kind in ("personal_adapter", "qwen_base", "qwen_teacher", "premature_prototype"):
        row = conn.execute(
            """
            SELECT model_version, model_kind, metadata_json
            FROM coach_models
            WHERE model_kind = ? AND is_active = 1
            ORDER BY created_at DESC, id DESC
            LIMIT 1
            """,
            (model_kind,),
        ).fetchone()
        if row:
            metadata = json.loads(row["metadata_json"] or "{}")
            return {
                "model_version": row["model_version"],
                "model_kind": row["model_kind"],
                "metadata": metadata,
            }
    return None


def stderr(message: str) -> None:
    print(message, file=sys.stderr)


def load_qwen_model(
    *,
    model_id: str,
    adapter_dir: str = "",
    device_map: str = "auto",
):
    require_packages(["torch", "transformers", "accelerate"], "Qwen inference")

    import torch
    from transformers import AutoProcessor

    model_cls = None
    try:
        from transformers import Qwen2_5_VLForConditionalGeneration as model_cls  # type: ignore
    except Exception:
        try:
            from transformers import AutoModelForImageTextToText as model_cls  # type: ignore
        except Exception as exc:  # pragma: no cover - depends on local env
            raise SystemExit(f"Could not load a Qwen-compatible model class from transformers: {exc}") from exc

    model = model_cls.from_pretrained(
        model_id,
        torch_dtype="auto",
        device_map=device_map,
    )
    processor = AutoProcessor.from_pretrained(model_id)

    if adapter_dir:
        require_packages(["peft"], "Qwen adapter inference")
        from peft import PeftModel

        model = PeftModel.from_pretrained(model, adapter_dir)

    return model, processor


def run_qwen_generation(
    *,
    model,
    processor,
    image_paths: list[str],
    prompt: str,
    max_new_tokens: int = 256,
) -> str:
    messages = [
        {
            "role": "user",
            "content": [
                *[{"type": "image", "image": str(Path(path).resolve())} for path in image_paths],
                {"type": "text", "text": prompt},
            ],
        }
    ]

    text = processor.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)

    image_inputs = None
    video_inputs = None
    try:
        from qwen_vl_utils import process_vision_info  # type: ignore

        image_inputs, video_inputs = process_vision_info(messages)
    except Exception:
        image_inputs = [Image.open(path).convert("RGB") for path in image_paths]
        video_inputs = None

    inputs = processor(
        text=[text],
        images=image_inputs,
        videos=video_inputs,
        padding=True,
        return_tensors="pt",
    )

    model_device = getattr(model, "device", None)
    if model_device is not None:
        inputs = inputs.to(model_device)

    generated_ids = model.generate(**inputs, max_new_tokens=max_new_tokens)
    generated_ids_trimmed = [
        out_ids[len(in_ids) :] for in_ids, out_ids in zip(inputs.input_ids, generated_ids)
    ]
    output_text = processor.batch_decode(
        generated_ids_trimmed,
        skip_special_tokens=True,
        clean_up_tokenization_spaces=False,
    )
    return output_text[0] if output_text else ""
