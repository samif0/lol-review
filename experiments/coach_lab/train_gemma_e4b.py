from __future__ import annotations

import argparse
import json
import traceback
from pathlib import Path
from typing import Any

from gemma_stack import (
    DEFAULT_BASE_MODEL_ID,
    DEFAULT_DB_PATH,
    DEFAULT_EXPORT,
    MODELS_ROOT,
    DEFAULT_SYSTEM_PROMPT,
    ModelRecord,
    PROMPT_VERSION,
    ensure_dir,
    iter_clip_training_records,
    read_jsonl,
    register_model,
    require_packages,
    version_name,
    write_gemma_dataset,
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Prepare or run a Gemma 4 E4B QLoRA pilot for Coach Lab clip-card extraction."
    )
    parser.add_argument("--input", type=Path, default=DEFAULT_EXPORT / "moments.jsonl")
    parser.add_argument("--output", type=Path, default=MODELS_ROOT / "gemma")
    parser.add_argument("--db", type=Path, default=DEFAULT_DB_PATH)
    parser.add_argument("--model-id", default=DEFAULT_BASE_MODEL_ID)
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--register", action="store_true")
    parser.add_argument("--epochs", type=float, default=1.0)
    parser.add_argument("--learning-rate", type=float, default=1e-4)
    parser.add_argument("--batch-size", type=int, default=1)
    parser.add_argument("--gradient-accumulation-steps", type=int, default=4)
    parser.add_argument("--max-steps", type=int, default=0)
    parser.add_argument("--min-examples", type=int, default=2)
    parser.add_argument("--load-in-4bit", dest="load_in_4bit", action="store_true")
    parser.add_argument("--full-precision", dest="load_in_4bit", action="store_false")
    parser.set_defaults(load_in_4bit=True)
    return parser.parse_args()


def print_result(payload: dict[str, Any]) -> None:
    print(json.dumps(payload, ensure_ascii=True))


def discover_text_lora_targets(model: Any) -> list[str]:
    target_suffixes = (
        ".q_proj",
        ".k_proj",
        ".v_proj",
        ".o_proj",
        ".gate_proj",
        ".up_proj",
        ".down_proj",
    )
    targets = [
        module_name
        for module_name, _ in model.named_modules()
        if module_name.startswith("model.language_model.layers.")
        and module_name.endswith(target_suffixes)
    ]
    return sorted(targets)


def build_payload(
    *,
    model_version: str,
    registered_model_version: str = "",
    trained: bool,
    registered: bool,
    reason: str,
    model_directory: Path | str,
    train_count: int,
    eval_count: int,
    gold_examples: int,
    silver_examples: int,
) -> dict[str, Any]:
    return {
        "ModelVersion": model_version,
        "RegisteredModelVersion": registered_model_version,
        "Trained": trained,
        "Registered": registered,
        "Reason": reason,
        "ModelDirectory": str(model_directory),
        "TrainCount": train_count,
        "EvalCount": eval_count,
        "GoldExamples": gold_examples,
        "SilverExamples": silver_examples,
    }


def main() -> None:
    args = parse_args()

    if not args.input.exists():
        print_result(
            build_payload(
                model_version="",
                trained=False,
                registered=False,
                reason=f"missing_input:{args.input}",
                model_directory=args.output,
                train_count=0,
                eval_count=0,
                gold_examples=0,
                silver_examples=0,
            )
        )
        return

    records = read_jsonl(args.input)
    training_records = iter_clip_training_records(records, gold_only=False, include_silver=True)
    if len(training_records) < args.min_examples:
        print_result(
            build_payload(
                model_version="",
                trained=False,
                registered=False,
                reason=f"not_enough_examples:{len(training_records)}<{args.min_examples}",
                model_directory=args.output,
                train_count=0,
                eval_count=0,
                gold_examples=0,
                silver_examples=0,
            )
        )
        return

    model_version = version_name("gemma-e4b-adapter")
    model_root = ensure_dir(args.output / model_version)
    dataset_info = write_gemma_dataset(training_records, model_root / "dataset")
    adapter_dir = ensure_dir(model_root / "adapter")

    metadata: dict[str, Any] = {
        "family": "gemma4",
        "role": "coach_adapter",
        "hf_model_id": args.model_id,
        "adapter_dir": str(adapter_dir),
        "dataset_dir": str(model_root / "dataset"),
        "prompt_version": PROMPT_VERSION,
        "system_prompt": DEFAULT_SYSTEM_PROMPT,
        "train_count": dataset_info["train_count"],
        "eval_count": dataset_info["eval_count"],
        "gold_examples": dataset_info["gold_examples"],
        "silver_examples": dataset_info["silver_examples"],
        "status": "prepared",
    }

    trained = False
    reason = "prepare_only_requested" if args.prepare_only else ""
    train_traceback = ""
    if not args.prepare_only:
        try:
            train_with_qlora(
                model_id=args.model_id,
                dataset_dir=model_root / "dataset",
                output_dir=adapter_dir,
                epochs=args.epochs,
                learning_rate=args.learning_rate,
                batch_size=args.batch_size,
                gradient_accumulation_steps=args.gradient_accumulation_steps,
                max_steps=args.max_steps,
                load_in_4bit=args.load_in_4bit,
            )
            trained = True
            reason = "trained"
            metadata["status"] = "trained"
        except SystemExit as exc:
            reason = str(exc) or "training_skipped"
        except Exception as exc:  # pragma: no cover - environment-dependent
            reason = f"training_failed:{exc}"
            train_traceback = traceback.format_exc()

    if train_traceback:
        (model_root / "train-error.txt").write_text(train_traceback, encoding="utf-8")

    registered = False
    registered_model_version = ""
    if trained and args.register:
        register_model(
            args.db,
            ModelRecord(
                model_version=model_version,
                model_kind="gemma_adapter",
                display_name=f"Gemma Coach Adapter ({dataset_info['train_count']} clips)",
                provider="transformers-peft-bitsandbytes",
                metadata=metadata,
                is_active=True,
            ),
        )
        registered = True
        registered_model_version = model_version

    (model_root / "metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    print_result(
        build_payload(
            model_version=model_version,
            registered_model_version=registered_model_version,
            trained=trained,
            registered=registered,
            reason=reason or "prepared_only",
            model_directory=model_root,
            train_count=dataset_info["train_count"],
            eval_count=dataset_info["eval_count"],
            gold_examples=dataset_info["gold_examples"],
            silver_examples=dataset_info["silver_examples"],
        )
    )


def train_with_qlora(
    *,
    model_id: str,
    dataset_dir: Path,
    output_dir: Path,
    epochs: float,
    learning_rate: float,
    batch_size: int,
    gradient_accumulation_steps: int,
    max_steps: int,
    load_in_4bit: bool,
) -> None:
    require_packages(
        ["torch", "transformers", "accelerate", "peft", "trl", "bitsandbytes"],
        "Gemma QLoRA training",
    )

    import torch
    from PIL import Image
    from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training
    from torch.utils.data import Dataset
    from transformers import (
        AutoModelForImageTextToText,
        AutoProcessor,
        BitsAndBytesConfig,
        Trainer,
        TrainingArguments,
    )

    train_rows = read_jsonl(dataset_dir / "train.jsonl")
    eval_path = dataset_dir / "eval.jsonl"
    eval_rows = read_jsonl(eval_path) if eval_path.exists() else []

    if not train_rows:
        raise SystemExit("no_train_rows")

    processor = AutoProcessor.from_pretrained(model_id)
    if getattr(processor.tokenizer, "pad_token", None) is None:
        processor.tokenizer.pad_token = processor.tokenizer.eos_token

    model_kwargs: dict[str, Any] = {
        "device_map": "auto",
        "torch_dtype": torch.bfloat16,
    }
    if load_in_4bit:
        model_kwargs["quantization_config"] = BitsAndBytesConfig(
            load_in_4bit=True,
            bnb_4bit_use_double_quant=True,
            bnb_4bit_quant_type="nf4",
            bnb_4bit_compute_dtype=torch.bfloat16,
            bnb_4bit_quant_storage=torch.bfloat16,
        )

    model = AutoModelForImageTextToText.from_pretrained(model_id, **model_kwargs)
    model.config.use_cache = False
    if load_in_4bit:
        model = prepare_model_for_kbit_training(model)

    target_modules = discover_text_lora_targets(model)
    if not target_modules:
        raise SystemExit("no_supported_language_lora_targets")

    lora_config = LoraConfig(
        r=8,
        lora_alpha=16,
        lora_dropout=0.05,
        bias="none",
        task_type="CAUSAL_LM",
        # Gemma 4 multimodal models expose similarly named vision modules that PEFT cannot wrap.
        # Use the discovered language-model projection modules only.
        target_modules=target_modules,
    )
    model = get_peft_model(model, lora_config)
    model.gradient_checkpointing_enable()

    class GemmaVisionDataset(Dataset):
        def __init__(self, rows: list[dict[str, Any]]) -> None:
            self.rows = rows

        def __len__(self) -> int:
            return len(self.rows)

        def __getitem__(self, idx: int) -> dict[str, Any]:
            return self.rows[idx]

    class GemmaVisionCollator:
        def __init__(self, model_processor) -> None:
            self.processor = model_processor
            self.pad_token_id = self.processor.tokenizer.pad_token_id

        def __call__(self, features: list[dict[str, Any]]) -> dict[str, Any]:
            images = []
            prompt_texts = []
            full_texts = []
            for feature in features:
                images.append(Image.open(feature["image_path"]).convert("RGB"))

                prompt_messages = [
                    {"role": "system", "content": feature["system_prompt"]},
                    {
                        "role": "user",
                        "content": [
                            {"type": "image"},
                            {"type": "text", "text": feature["user_prompt"]},
                        ],
                    },
                ]
                full_messages = prompt_messages + [
                    {
                        "role": "assistant",
                        "content": [{"type": "text", "text": feature["assistant_response"]}],
                    }
                ]

                prompt_texts.append(
                    self.processor.apply_chat_template(
                        prompt_messages,
                        tokenize=False,
                        add_generation_prompt=True,
                    )
                )
                full_texts.append(
                    self.processor.apply_chat_template(
                        full_messages,
                        tokenize=False,
                        add_generation_prompt=False,
                    )
                )

            batch = self.processor(
                text=full_texts,
                images=images,
                padding=True,
                return_tensors="pt",
            )
            prompt_batch = self.processor(
                text=prompt_texts,
                images=images,
                padding=True,
                return_tensors="pt",
            )

            labels = batch["input_ids"].clone()
            labels[labels == self.pad_token_id] = -100
            for index in range(labels.shape[0]):
                prompt_length = int((prompt_batch["input_ids"][index] != self.pad_token_id).sum().item())
                labels[index, :prompt_length] = -100
            batch["labels"] = labels
            return batch

    training_args = TrainingArguments(
        output_dir=str(output_dir),
        per_device_train_batch_size=batch_size,
        per_device_eval_batch_size=batch_size,
        gradient_accumulation_steps=gradient_accumulation_steps,
        learning_rate=learning_rate,
        num_train_epochs=epochs,
        max_steps=max_steps if max_steps > 0 else -1,
        logging_steps=1,
        save_strategy="epoch",
        evaluation_strategy="epoch" if eval_rows else "no",
        remove_unused_columns=False,
        bf16=torch.cuda.is_available(),
        report_to=[],
    )

    trainer = Trainer(
        model=model,
        args=training_args,
        train_dataset=GemmaVisionDataset(train_rows),
        eval_dataset=GemmaVisionDataset(eval_rows) if eval_rows else None,
        data_collator=GemmaVisionCollator(processor),
    )
    trainer.train()
    model.save_pretrained(str(output_dir))
    processor.save_pretrained(str(output_dir))


if __name__ == "__main__":
    main()
