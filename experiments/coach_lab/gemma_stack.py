from __future__ import annotations

import importlib.util
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

DEFAULT_BASE_MODEL_ID = "google/gemma-4-E4B-it"
DEFAULT_SYSTEM_PROMPT = (
    "You are an elite League of Legends lane coach. Stay grounded in the supplied evidence. "
    "Return strict JSON only."
)
PROMPT_VERSION = "coach-gemma-v1"

OBJECTIVE_KEYS = {
    "favorable_trade_windows",
    "respect_jungle_support_threat",
    "safe_lane_spacing",
    "recall_on_crash_and_tempo",
    "punish_enemy_cooldown_windows",
}

@dataclass(slots=True)
class ModelRecord:
    model_version: str
    model_kind: str
    display_name: str
    provider: str
    metadata: dict[str, Any]
    is_active: bool = True


def dependency_status() -> dict[str, bool]:
    packages = [
        "torch",
        "transformers",
        "accelerate",
        "bitsandbytes",
        "peft",
        "trl",
    ]
    return {package: importlib.util.find_spec(package) is not None for package in packages}


def require_packages(packages: Iterable[str], feature_name: str) -> None:
    status = dependency_status()
    missing = [package for package in packages if not status.get(package, False)]
    if not missing:
        return

    raise SystemExit(
        f"{feature_name} requires missing Python packages: {', '.join(missing)}. "
        "Run setup_gemma_stack.ps1 first, then retry."
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


def stderr(message: str) -> None:
    print(message, file=sys.stderr)


def extract_supervision(record: dict[str, Any]) -> dict[str, Any]:
    supervision = record.get("supervision", {})
    manual = supervision.get("manual_label")
    draft = supervision.get("draft_label")
    chosen = manual or draft or {}
    clip_note = trim_text(str(supervision.get("clip_note") or ""), 240)
    fallback_reason = trim_text(
        chosen.get("primary_reason")
        or chosen.get("explanation")
        or chosen.get("rationale")
        or "",
        240,
    )
    return {
        "quality": (chosen.get("quality") or "neutral").strip().lower(),
        "primary_reason": clip_note or fallback_reason,
        "objective_key": (chosen.get("objective_key") or "").strip(),
        "confidence": float(chosen.get("confidence") or 0.7),
        "rationale": trim_text(
            chosen.get("explanation")
            or chosen.get("rationale")
            or supervision.get("clip_note")
            or ""
        ),
        "clip_note": clip_note,
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


def build_review_context(record: dict[str, Any]) -> str:
    supervision = record.get("supervision", {})
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
    return trim_text(review_context, 500)


def build_clip_prompt(record: dict[str, Any], *, include_hints: bool) -> str:
    training_input = record.get("training_input", {})
    champion = training_input.get("champion") or "Unknown"
    role = training_input.get("role") or "adc"
    game_time_s = int(training_input.get("game_time_s") or 0)
    active_objective = training_input.get("active_objective_title") or ""
    clip_note = trim_text(str(record.get("supervision", {}).get("clip_note") or ""), 240)
    review_context = build_review_context(record) if include_hints else ""

    prompt = f"""
You are reviewing one short League of Legends lane clip represented by a storyboard and minimap composite image.

Metadata:
- champion: {champion}
- role: {role}
- game_time_s: {game_time_s}
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
  "primary_reason": "short free-form clip note or moment explanation",
  "objective_key": "string",
  "confidence": 0.0,
  "rationale": "short one-sentence explanation"
}

Rules:
- stay grounded in the image and supplied metadata
- if a clip note is supplied, preserve its meaning in primary_reason
- keep rationale short
- objective_key should be empty unless the moment clearly supports a known objective
"""
    return prompt.strip()


def target_output(record: dict[str, Any]) -> str:
    chosen = extract_supervision(record)
    objective_key = chosen["objective_key"]
    primary_reason = chosen["clip_note"] or chosen["primary_reason"] or "Needs manual clip note."
    if not objective_key and primary_reason in OBJECTIVE_KEYS:
        objective_key = primary_reason

    payload = {
        "moment_quality": chosen["quality"] or "neutral",
        "primary_reason": primary_reason,
        "objective_key": objective_key,
        "confidence": round(float(chosen["confidence"] or 0.7), 2),
        "rationale": chosen["rationale"] or primary_reason,
    }
    return json.dumps(payload, ensure_ascii=True)


def build_composite_image(storyboard_path: str | Path, minimap_path: str | Path) -> Image.Image:
    storyboard = Image.open(storyboard_path).convert("RGB")
    minimap = Image.open(minimap_path).convert("RGB")

    width = max(storyboard.width, minimap.width)
    if storyboard.width != width:
        storyboard = storyboard.resize(
            (width, max(1, int(round(storyboard.height * width / storyboard.width)))),
            Image.Resampling.LANCZOS,
        )
    if minimap.width != width:
        minimap = minimap.resize(
            (width, max(1, int(round(minimap.height * width / minimap.width)))),
            Image.Resampling.LANCZOS,
        )

    padding = 16
    canvas = Image.new(
        "RGB",
        (width, storyboard.height + minimap.height + padding * 3),
        color=(18, 18, 24),
    )
    canvas.paste(storyboard, (0, padding))
    canvas.paste(minimap, (0, storyboard.height + padding * 2))
    return canvas


def split_train_eval(
    records: list[dict[str, Any]],
    eval_fraction: float = 0.15,
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
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


def build_training_sample(record: dict[str, Any], composite_path: Path) -> dict[str, Any]:
    supervision = extract_supervision(record)
    return {
        "image_path": str(composite_path),
        "system_prompt": DEFAULT_SYSTEM_PROMPT,
        "user_prompt": build_clip_prompt(record, include_hints=True),
        "assistant_response": target_output(record),
        "metadata": {
            "moment_id": record.get("moment_id"),
            "game_id": record.get("game_id"),
            "source_type": record.get("source_type"),
            "supervision_bucket": supervision["bucket"],
            "quality": supervision["quality"],
            "primary_reason": supervision["primary_reason"],
            "objective_key": supervision["objective_key"],
        },
    }


def count_bucket_stats(records: Iterable[dict[str, Any]]) -> tuple[int, int]:
    gold = 0
    silver = 0
    for record in records:
        bucket = extract_supervision(record)["bucket"]
        if bucket == "gold":
            gold += 1
        elif bucket == "silver":
            silver += 1
    return gold, silver


def write_gemma_dataset(records: list[dict[str, Any]], output_dir: Path) -> dict[str, Any]:
    output_dir = ensure_dir(output_dir)
    images_dir = ensure_dir(output_dir / "images")
    train_rows, eval_rows = split_train_eval(records)

    def materialize(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
        materialized: list[dict[str, Any]] = []
        for record in rows:
            moment_id = int(record.get("moment_id") or 0)
            artifacts = record.get("training_input", {}).get("artifacts", {})
            composite_path = images_dir / f"{moment_id}.jpg"
            image = build_composite_image(
                artifacts.get("storyboard_path", ""),
                artifacts.get("minimap_strip_path", ""),
            )
            image.save(composite_path, format="JPEG", quality=92)
            materialized.append(build_training_sample(record, composite_path))
        return materialized

    train_dataset = materialize(train_rows)
    eval_dataset = materialize(eval_rows)

    train_path = output_dir / "train.jsonl"
    eval_path = output_dir / "eval.jsonl"
    write_jsonl(train_path, train_dataset)
    if eval_dataset:
        write_jsonl(eval_path, eval_dataset)
    elif eval_path.exists():
        eval_path.unlink()

    gold, silver = count_bucket_stats(records)
    return {
        "train_path": str(train_path),
        "eval_path": str(eval_path) if eval_dataset else "",
        "train_count": len(train_dataset),
        "eval_count": len(eval_dataset),
        "gold_examples": gold,
        "silver_examples": silver,
    }


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


def normalize_prediction(payload: dict[str, Any], *, model_version: str) -> dict[str, Any]:
    primary_reason = trim_text(
        str(payload.get("primary_reason") or payload.get("rationale") or "").strip(),
        240,
    )
    objective_key = str(payload.get("objective_key") or "").strip()

    if not objective_key and primary_reason in OBJECTIVE_KEYS:
        objective_key = primary_reason

    return {
        "ModelVersion": model_version,
        "MomentQuality": str(payload.get("moment_quality") or "neutral").strip().lower() or "neutral",
        "PrimaryReason": primary_reason,
        "ObjectiveKey": objective_key,
        "Confidence": max(0.0, min(1.0, float(payload.get("confidence") or 0.5))),
        "Rationale": trim_text(str(payload.get("rationale") or ""), 240),
    }


def load_gemma_model(
    *,
    model_id: str,
    adapter_dir: str = "",
    device_map: str = "auto",
    load_in_4bit: bool = False,
):
    require_packages(["torch", "transformers", "accelerate"], "Gemma inference")

    import torch
    from transformers import AutoModelForImageTextToText, AutoProcessor

    model_kwargs: dict[str, Any] = {
        "device_map": device_map,
        "torch_dtype": torch.bfloat16,
    }

    if load_in_4bit:
        require_packages(["bitsandbytes"], "Gemma 4-bit inference")
        from transformers import BitsAndBytesConfig

        model_kwargs["quantization_config"] = BitsAndBytesConfig(
            load_in_4bit=True,
            bnb_4bit_use_double_quant=True,
            bnb_4bit_quant_type="nf4",
            bnb_4bit_compute_dtype=torch.bfloat16,
            bnb_4bit_quant_storage=torch.bfloat16,
        )

    model = AutoModelForImageTextToText.from_pretrained(model_id, **model_kwargs)
    processor = AutoProcessor.from_pretrained(model_id)

    if adapter_dir:
        require_packages(["peft"], "Gemma adapter inference")
        from peft import PeftModel

        model = PeftModel.from_pretrained(model, adapter_dir)

    return model, processor


def run_gemma_generation(
    *,
    model,
    processor,
    prompt: str,
    system_prompt: str = DEFAULT_SYSTEM_PROMPT,
    image: Image.Image | None = None,
    max_new_tokens: int = 256,
) -> str:
    user_content: list[dict[str, str]] = []
    if image is not None:
        user_content.append({"type": "image"})
    user_content.append({"type": "text", "text": prompt})

    messages: list[dict[str, Any]] = []
    if system_prompt:
        messages.append({"role": "system", "content": system_prompt})
    messages.append({"role": "user", "content": user_content})

    text = processor.apply_chat_template(
        messages,
        tokenize=False,
        add_generation_prompt=True,
    )

    if image is not None:
        inputs = processor(
            text=[text],
            images=[image],
            padding=True,
            return_tensors="pt",
        )
    else:
        inputs = processor(
            text=[text],
            padding=True,
            return_tensors="pt",
        )

    model_device = getattr(model, "device", None)
    if model_device is not None:
        inputs = inputs.to(model_device)

    generated_ids = model.generate(
        **inputs,
        max_new_tokens=max_new_tokens,
        do_sample=False,
    )
    generated_ids_trimmed = [
        out_ids[len(in_ids):] for in_ids, out_ids in zip(inputs.input_ids, generated_ids)
    ]
    output_text = processor.batch_decode(
        generated_ids_trimmed,
        skip_special_tokens=True,
        clean_up_tokenization_spaces=False,
    )
    return output_text[0] if output_text else ""
