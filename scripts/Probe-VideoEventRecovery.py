"""Local-only, offline feasibility probe. Writes evidence cards, never game events.

Run a cached vision-language model against specified VOD windows. Requires a CUDA
PyTorch/Transformers environment; refuses CPU fallback and network model downloads.
All candidates remain unverified, regardless of model confidence.
"""
import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path


def validate_card(response, champion, allies, enemies, frame_count):
    """Structural rejection only; a passing card still requires visual validation."""
    text = response.strip()
    if text.startswith("```json") and text.endswith("```"):
        text = text[7:-3].strip()
    try:
        card = json.loads(text)
        if not isinstance(card, dict):
            raise ValueError("Expected object")
        normalize = lambda value: "".join(c for c in value.casefold() if c.isalnum())
        own = normalize(champion)
        blue = {normalize(name) for name in allies} | {own}
        red = {normalize(name) for name in enemies}
        reasons = []
        directions = set()
        if normalize(card.get("local_champion", "")) != own:
            reasons.append("Local champion mismatch")
        for direction in card.get("directions", []):
            attacker, target = normalize(direction["attacker"]), normalize(direction["target"])
            if not ((attacker in blue and target in red) or (attacker in red and target in blue)):
                reasons.append("Damage direction contradicts roster")
            frames = direction["frames"]
            if not frames or any(type(frame) is not int or not 1 <= frame <= frame_count for frame in frames):
                reasons.append("Invalid supporting frame indices")
            directions.add((attacker, target))
        classification = card.get("classification")
        if classification == "reciprocal_exchange" and not any(
                (own, opponent) in directions and (opponent, own) in directions for opponent in red):
            reasons.append("No reciprocal local-player/opponent directions")
        if classification not in ("reciprocal_exchange", "one_way_poke", "no_visible_champion_interaction", "unknown"):
            reasons.append("Unknown classification")
        if classification == "one_way_poke" and not any(own in pair for pair in directions):
            reasons.append("No local-player interaction")
        if classification == "no_visible_champion_interaction" and directions:
            reasons.append("Contradictory no-interaction classification")
        return {"card":card,"structurally_valid":not reasons,"rejection_reasons":sorted(set(reasons)),
                "eligible_for_publication":False,"requires_visual_validation":True}
    except (ValueError, TypeError, KeyError, AttributeError) as error:
        return {"structurally_valid":False,"rejection_reasons":[f"Invalid model schema: {type(error).__name__}"],
                "eligible_for_publication":False,"requires_visual_validation":True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--video", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--windows", required=True, help="video start:end pairs separated by commas")
    parser.add_argument("--champion", required=True)
    parser.add_argument("--allies", required=True, help="Comma-separated allied champion names")
    parser.add_argument("--enemies", required=True, help="Comma-separated enemy champion names")
    parser.add_argument("--extra-packages")
    args = parser.parse_args()
    import torch
    if args.extra_packages:
        sys.path.append(args.extra_packages)
    from PIL import Image
    from transformers import AutoProcessor, AutoModelForImageTextToText
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA required; no CPU fallback during this probe")
    torch.set_num_threads(2)
    os.environ["HF_HUB_OFFLINE"] = "1"
    windows = [tuple(map(float, pair.split(":"))) for pair in args.windows.split(",")]
    if not 1 <= len(windows) <= 6 or any(a < 0 or b <= a or b-a > 20 for a,b in windows):
        raise ValueError("Use 1-6 windows, each at most 20 seconds")
    root = Path(args.output).resolve()
    root.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    model = AutoModelForImageTextToText.from_pretrained(
        args.model, local_files_only=True, dtype=torch.bfloat16,
        attn_implementation="sdpa").to("cuda").eval()
    processor = AutoProcessor.from_pretrained(args.model, local_files_only=True,
                                             min_pixels=256*28*28, max_pixels=640*28*28)
    loaded = time.monotonic()-started
    print(json.dumps({"model_loaded_seconds": loaded}), flush=True)
    reports = []
    for index,(start,end) in enumerate(windows):
        # Start a distinct directory; no stale frames from prior windows.
        directory = root / f"window-{index}-{time.time_ns()}"
        directory.mkdir()
        frame_count = 8
        fps = frame_count/(end-start)
        subprocess.run(["ffmpeg", "-v", "error", "-threads", "1", "-ss", str(start),
                        "-i", args.video, "-t", str(end-start), "-vf", f"fps={fps},scale=1280:-2",
                        "-frames:v", str(frame_count), "-threads", "1", str(directory/"frame-%02d.jpg")],
                       check=True, timeout=60, capture_output=True)
        paths = sorted(directory.glob("frame-*.jpg"))
        if len(paths) != frame_count:
            raise ValueError("Incomplete video window")
        prompt = (
            f"These are chronological sampled frames from a League of Legends recording. The local player is {args.champion}. "
            f"Allied roster: {args.allies}. Enemy roster: {args.enemies}. Allies cannot damage each other. "
            "Analyze only visible evidence. Reductions in health alone do not prove champion damage: minions, turrets and other sources exist. "
            "Do not infer a trade from proximity, button presses or a kill. A reciprocal exchange requires visible outgoing AND returning "
            "champion attacks connecting. Name each visible direction and supporting frame numbers. If uncertain return unknown. "
            "Do not infer exact fight numbers, unseen champions or readiness. Return only JSON: "
            '{"classification":"reciprocal_exchange|one_way_poke|no_visible_champion_interaction|unknown",'
            '"local_champion":"name","visible_champions":[],"directions":[{"attacker":"name",'
            '"target":"name","frames":[1],"evidence":"visible attack evidence"}],"limitations":[],"reason":"brief explanation"}. '
            "Frame labels are approximate video timestamps, not authoritative game-clock timestamps."
        )
        content = [{"type":"text","text":prompt}]
        images = []
        for number,path in enumerate(paths,1):
            images.append(Image.open(path).convert("RGB"))
            content.extend([{"type":"text","text":f"Frame {number}, video approximately {start+(number-.5)/fps:.2f}s"},
                            {"type":"image","image":str(path)}])
        text = processor.apply_chat_template([{"role":"user","content":content}],
                                            tokenize=False, add_generation_prompt=True)
        inputs = processor(text=[text], images=images, return_tensors="pt", padding=True).to("cuda")
        torch.cuda.reset_peak_memory_stats()
        before = time.monotonic()
        with torch.inference_mode():
            generated = model.generate(**inputs, max_new_tokens=400, do_sample=False)
        torch.cuda.synchronize()
        response = processor.batch_decode(generated[:,inputs.input_ids.shape[1]:], skip_special_tokens=True)[0]
        report = {"version":1,"verification":"unverified","shadow":True,
                  "model":str(Path(args.model).resolve()),"video":str(Path(args.video).resolve()),
                  "window_video_seconds":[start,end],"sampled_frames":[str(p) for p in paths],
                  "prompt":prompt,"roster":{"allies":args.allies.split(","),"enemies":args.enemies.split(",")},
                  "runtime":{"torch":torch.__version__,"transformers":__import__('transformers').__version__},
                  "inference_seconds":round(time.monotonic()-before,3),
                  "peak_allocated_gpu_mb":round(torch.cuda.max_memory_allocated()/1024**2,1),
                  "response":response,
                  "validation":validate_card(response,args.champion,args.allies.split(","),args.enemies.split(","),frame_count)}
        reports.append(report)
        (root/"probe-results.json").write_text(json.dumps({"model_load_seconds":loaded,"windows":reports},indent=2),encoding="utf-8")
        print(json.dumps(report),flush=True)
        del inputs, generated
        for image in images: image.close()


if __name__ == "__main__":
    main()
