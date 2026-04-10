from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any

from gemma_stack import (
    DEFAULT_BASE_MODEL_ID,
    DEFAULT_SYSTEM_PROMPT,
    build_composite_image,
    extract_json_object,
    load_gemma_model,
    normalize_prediction,
    run_gemma_generation,
    stderr,
    trim_text,
)


PROTOCOL_PREFIX = "__coach_json__"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Persistent Gemma worker for Coach Lab drafting and bundle planning."
    )
    parser.add_argument("--model-id", default=DEFAULT_BASE_MODEL_ID)
    parser.add_argument("--model-version", required=True)
    parser.add_argument("--adapter-dir", default="")
    parser.add_argument("--load-in-4bit", action="store_true")
    return parser.parse_args()


def emit(payload: dict[str, Any]) -> None:
    print(PROTOCOL_PREFIX + json.dumps(payload, ensure_ascii=True), flush=True)


def humanize(value: str) -> str:
    value = (value or "").strip()
    if not value:
        return "Needs review"
    return " ".join(part.capitalize() for part in value.split("_"))


def build_draft_prompt(request: dict[str, Any]) -> str:
    champion = request.get("champion") or "Unknown"
    role = request.get("role") or "adc"
    game_time_s = int(request.get("game_time_s") or 0)
    active_objective = request.get("active_objective_title") or ""
    note_text = trim_text(request.get("note_text") or "", 300)
    review_context = trim_text(request.get("review_context") or "", 450)

    prompt = f"""
You are reviewing one short League of Legends lane clip represented by a storyboard and minimap composite image.

Metadata:
- champion: {champion}
- role: {role}
- game_time_s: {game_time_s}
"""
    if active_objective:
        prompt += f'- current objective title: "{active_objective}"\n'
    if note_text:
        prompt += f'- clip note: "{note_text}"\n'
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
- stay grounded in the image and metadata
- if a clip note is supplied, preserve its meaning in primary_reason
- keep rationale short
- objective_key should be empty unless the moment clearly supports a known objective
"""
    return prompt.strip()


def build_problem_evidence(clip_cards: list[dict[str, Any]]) -> list[dict[str, Any]]:
    focus_cards = [card for card in clip_cards if str(card.get("moment_quality") or "").lower() == "bad"]
    cards = focus_cards or clip_cards
    evidence: list[dict[str, Any]] = []
    for card in cards[:8]:
        evidence.append(
            {
                "moment_id": int(card.get("moment_id") or 0),
                "game_id": int(card.get("game_id") or 0),
                "moment_quality": str(card.get("moment_quality") or "neutral"),
                "clip_note": trim_text(str(card.get("reason_key") or card.get("evidence") or ""), 180),
                "objective_key": str(card.get("objective_key") or ""),
                "attached_objective_title": str(card.get("attached_objective_title") or ""),
                "confidence": round(float(card.get("confidence") or 0.5), 2),
            }
        )
    return evidence


def build_problems_prompt(request: dict[str, Any], evidence: list[dict[str, Any]]) -> str:
    return f"""
You are summarizing recurring League of Legends coaching problems from clip-card evidence.

Current objective:
- title: {request.get("active_objective_title") or "None"}
- key: {request.get("active_objective_key") or ""}

Review context:
{trim_text(request.get("review_context") or "", 700) or "None"}

Clip evidence:
{json.dumps(evidence, ensure_ascii=True)}

Return strict JSON:
{{
  "title": "Recurring problems",
  "summary": "2-4 sentences grounded in the evidence above",
  "problems": [
    {{
      "reason_key": "optional free-form tag",
      "title": "short theme title",
      "moment_count": 0,
      "game_count": 0,
      "confidence": 0.0,
      "example_note": "short quote or paraphrase from one clip note"
    }}
  ]
}}
""".strip()


def count_candidate_evidence(
    clip_cards: list[dict[str, Any]],
    candidate: dict[str, Any],
    evidence_clip_ids: list[int],
) -> tuple[int, int]:
    if evidence_clip_ids:
        matched = [card for card in clip_cards if int(card.get("moment_id") or 0) in evidence_clip_ids]
        if matched:
            return len(matched), len({int(card.get("game_id") or 0) for card in matched})

    candidate_type = candidate.get("candidate_type") or ""
    objective_id = candidate.get("objective_id")
    objective_key = str(candidate.get("objective_key") or "")

    if candidate_type == "keep_current":
        matched = clip_cards
    elif candidate_type == "use_existing":
        matched = [
            card for card in clip_cards
            if objective_id is not None and int(card.get("attached_objective_id") or -1) == int(objective_id)
        ]
    else:
        matched = [
            card for card in clip_cards
            if str(card.get("moment_quality") or "").lower() == "bad"
        ]
        unattached = [
            card for card in matched
            if not card.get("attached_objective_id")
        ]
        if unattached:
            matched = unattached

    return len(matched), len({int(card.get("game_id") or 0) for card in matched})


def build_objective_prompt(request: dict[str, Any]) -> str:
    clip_cards = request.get("clip_cards") or []
    candidates = request.get("candidates") or []
    return f"""
You are choosing the best next coaching objective from a bounded shortlist.

Current objective:
- id: {request.get("active_objective_id")}
- title: {request.get("active_objective_title") or "None"}
- key: {request.get("active_objective_key") or ""}

Review context:
{trim_text(request.get("review_context") or "", 800) or "None"}

Recent clip cards:
{json.dumps(clip_cards, ensure_ascii=True)}

Candidate shortlist:
{json.dumps(candidates, ensure_ascii=True)}

Return strict JSON:
{{
  "decision": "keep_current|use_existing|create_new",
  "candidate_key": "one of the supplied candidate_key values",
  "why": "short grounded explanation",
  "evidence_clip_ids": [1, 2],
  "confidence": 0.0,
  "follow_up_metric": "short metric to watch next",
  "proposed_title": "required only when decision is create_new",
  "proposed_description": "required only when decision is create_new",
  "proposed_completion_criteria": "required only when decision is create_new"
}}
""".strip()


def handle_draft(request: dict[str, Any], model, processor, model_version: str) -> dict[str, Any]:
    composite = build_composite_image(request["storyboard"], request["minimap"])
    prompt = build_draft_prompt(request)
    raw_output = run_gemma_generation(
        model=model,
        processor=processor,
        system_prompt=DEFAULT_SYSTEM_PROMPT,
        prompt=prompt,
        image=composite,
        max_new_tokens=220,
    )
    payload = extract_json_object(raw_output)
    normalized = normalize_prediction(payload, model_version=model_version)
    return {"ok": True, **normalized}


def handle_problems(request: dict[str, Any], model, processor, model_version: str) -> dict[str, Any]:
    clip_cards = list(request.get("clip_cards") or [])
    evidence = build_problem_evidence(clip_cards)
    prompt = build_problems_prompt(request, evidence)
    raw_output = run_gemma_generation(
        model=model,
        processor=processor,
        system_prompt=DEFAULT_SYSTEM_PROMPT,
        prompt=prompt,
        image=None,
        max_new_tokens=220,
    )
    payload = extract_json_object(raw_output)
    problem_rows: list[dict[str, Any]] = []
    for raw_problem in payload.get("problems") or []:
        if not isinstance(raw_problem, dict):
            continue
        problem_rows.append(
            {
                "ReasonKey": trim_text(str(raw_problem.get("reason_key") or ""), 80),
                "Title": trim_text(str(raw_problem.get("title") or "Recurring issue"), 80),
                "MomentCount": max(0, int(raw_problem.get("moment_count") or 0)),
                "GameCount": max(0, int(raw_problem.get("game_count") or 0)),
                "Confidence": max(0.0, min(1.0, float(raw_problem.get("confidence") or 0.5))),
                "ExampleNote": trim_text(str(raw_problem.get("example_note") or ""), 160),
            }
        )
    return {
        "ok": True,
        "ModelVersion": model_version,
        "Title": trim_text(str(payload.get("title") or "Recurring problems"), 80),
        "Summary": trim_text(str(payload.get("summary") or "Gemma could not summarize the current problem evidence."), 1200),
        "Problems": problem_rows,
    }


def handle_objective_plan(request: dict[str, Any], model, processor, model_version: str) -> dict[str, Any]:
    clip_cards = list(request.get("clip_cards") or [])
    candidates = list(request.get("candidates") or [])
    if not candidates:
        raise ValueError("Objective planning requires at least one candidate.")

    prompt = build_objective_prompt(request)
    raw_output = run_gemma_generation(
        model=model,
        processor=processor,
        system_prompt=DEFAULT_SYSTEM_PROMPT,
        prompt=prompt,
        image=None,
        max_new_tokens=260,
    )
    payload = extract_json_object(raw_output)

    candidate_key = str(payload.get("candidate_key") or "").strip()
    candidate = next((item for item in candidates if item.get("candidate_key") == candidate_key), None)
    if candidate is None:
        candidate = candidates[0]
        candidate_key = str(candidate.get("candidate_key") or "")

    decision = str(payload.get("decision") or candidate.get("candidate_type") or "keep_current").strip()
    if decision not in {"keep_current", "use_existing", "create_new"}:
        decision = str(candidate.get("candidate_type") or "keep_current")

    why = trim_text(str(payload.get("why") or "Gemma selected this candidate from the available evidence."), 500)
    evidence_clip_ids = [int(value) for value in (payload.get("evidence_clip_ids") or []) if str(value).isdigit()]
    confidence = max(0.0, min(1.0, float(payload.get("confidence") or 0.5)))
    follow_up_metric = trim_text(str(payload.get("follow_up_metric") or "Track recurrence of the same blocker over the next 3-5 games."), 160)
    proposed_title = trim_text(str(payload.get("proposed_title") or ""), 120)
    proposed_description = trim_text(str(payload.get("proposed_description") or ""), 400)
    proposed_completion_criteria = trim_text(str(payload.get("proposed_completion_criteria") or ""), 240)

    evidence_moment_count, evidence_game_count = count_candidate_evidence(clip_cards, candidate, evidence_clip_ids)
    title = (
        "Keep the current objective" if decision == "keep_current"
        else "Switch to an existing objective" if decision == "use_existing"
        else "Create a new objective"
    )

    candidate_title = str(candidate.get("title") or "")
    if decision == "create_new":
        candidate_title = proposed_title or candidate_title or "Create a new objective from recent clips"
    if decision == "keep_current":
        summary = f"Stay on \"{candidate_title or request.get('active_objective_title') or 'current objective'}\" for now. {why}"
    elif decision == "use_existing":
        summary = f"The strongest recurring blocker already lines up with \"{candidate_title}\". {why}"
    else:
        summary = f"The strongest recurring blocker is not attached to one of your current objectives yet. Suggested new objective: {candidate_title}. {why}"

    objective_key = str(payload.get("objective_key") or candidate.get("objective_key") or "")
    return {
        "ok": True,
        "ModelVersion": model_version,
        "Title": title,
        "Summary": trim_text(summary, 1200),
        "SuggestionMode": decision,
        "AttachedObjectiveId": candidate.get("objective_id"),
        "AttachedObjectiveTitle": candidate_title if decision != "create_new" else "",
        "ObjectiveKey": objective_key,
        "CandidateObjectiveTitle": candidate_title if decision == "create_new" else "",
        "CandidateCompletionCriteria": proposed_completion_criteria if decision == "create_new" else str(candidate.get("completion_criteria") or ""),
        "CandidateDescription": proposed_description if decision == "create_new" else str(candidate.get("description") or ""),
        "FollowUpMetric": follow_up_metric,
        "EvidenceMomentCount": evidence_moment_count,
        "EvidenceGameCount": evidence_game_count,
        "Confidence": round(confidence, 2),
        "EvidenceClipIds": evidence_clip_ids,
    }


def main() -> int:
    args = parse_args()

    try:
        model, processor = load_gemma_model(
            model_id=args.model_id,
            adapter_dir=args.adapter_dir,
            load_in_4bit=args.load_in_4bit,
        )
    except Exception as exc:  # pragma: no cover - environment-dependent
        stderr(str(exc))
        emit({"ready": False, "error": str(exc)})
        return 1

    emit(
        {
            "ready": True,
            "model_id": args.model_id,
            "model_version": args.model_version,
            "adapter_dir": args.adapter_dir,
        }
    )

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        try:
            request = json.loads(line)
            command = str(request.get("command") or "draft").strip().lower()
            if command == "shutdown":
                break
            if command == "draft":
                response = handle_draft(request, model, processor, args.model_version)
            elif command == "problems":
                response = handle_problems(request, model, processor, args.model_version)
            elif command == "objective_plan":
                response = handle_objective_plan(request, model, processor, args.model_version)
            else:
                raise ValueError(f"Unknown command: {command}")
            emit(response)
        except Exception as exc:  # pragma: no cover - exercised through runtime integration
            stderr(str(exc))
            emit(
                {
                    "ok": False,
                    "error": str(exc),
                    "ModelVersion": args.model_version,
                }
            )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
