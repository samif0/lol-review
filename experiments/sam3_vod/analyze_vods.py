from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import sqlite3
import subprocess
import sys
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any

VIDEO_EXTENSIONS = {".mp4", ".mkv", ".avi", ".webm", ".mov"}
EXPERIMENT_FLAG = "LOLREVIEW_ENABLE_SAM3_EXPERIMENT"
DEFAULT_ANALYSIS_SUBDIR = Path("vod-analysis") / "sam3-experiment"
DEFAULT_PROFILE_PATH = Path(__file__).with_name("profiles") / "league_default.json"


@dataclass(frozen=True)
class AppPaths:
    user_data_root: Path
    config_path: Path
    database_path: Path
    analysis_root: Path


@dataclass(frozen=True)
class LinkedGameInfo:
    game_id: int
    timestamp: int | None
    champion_name: str | None
    position: str | None
    win: int | None


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Local-only SAM3 VOD experiment runner for LoL Review."
    )
    parser.add_argument(
        "command",
        nargs="?",
        default="probe",
        choices=("probe", "plan", "run"),
        help="probe prints a summary, plan writes a manifest, run executes the selected backend.",
    )
    parser.add_argument(
        "--backend",
        default="dry-run",
        choices=("dry-run", "sam3"),
        help="dry-run prepares manifests only; sam3 attempts real model calls if installed.",
    )
    parser.add_argument(
        "--source",
        default="all",
        choices=("ascent", "linked", "all"),
        help="Select videos from the configured Ascent folder, linked DB rows, or both.",
    )
    parser.add_argument(
        "--ascent-folder",
        help="Override the Ascent folder instead of reading LoL Review config.json.",
    )
    parser.add_argument(
        "--video",
        action="append",
        default=[],
        help="Explicit video path to include. Repeat to include multiple files.",
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=3,
        help="Maximum number of videos to include when auto-discovering.",
    )
    parser.add_argument(
        "--profile",
        default=str(DEFAULT_PROFILE_PATH),
        help="Path to the prompt profile JSON file.",
    )
    parser.add_argument(
        "--output-root",
        help="Override the output root. Defaults to %%LOCALAPPDATA%%\\LoLReviewData\\vod-analysis\\sam3-experiment",
    )
    parser.add_argument(
        "--run-name",
        help="Optional run folder name. Defaults to a UTC timestamp.",
    )
    parser.add_argument(
        "--extract-previews",
        action="store_true",
        help="Use ffmpeg to extract preview frames at the sampled timestamps.",
    )
    parser.add_argument(
        "--preview-count",
        type=int,
        default=6,
        help="Fallback preview/sample count when the profile does not specify sample_seconds.",
    )
    parser.add_argument(
        "--ffprobe-bin",
        default="ffprobe",
        help="ffprobe executable name or full path.",
    )
    parser.add_argument(
        "--ffmpeg-bin",
        default="ffmpeg",
        help="ffmpeg executable name or full path.",
    )
    parser.add_argument(
        "--sam3-window-seconds",
        type=float,
        default=6.0,
        help="When using --backend sam3, extract a short window around each sample timestamp instead of loading the full VOD.",
    )
    parser.add_argument(
        "--sam3-clip-fps",
        type=float,
        default=2.0,
        help="Frame rate for the temporary clip windows passed to SAM3.",
    )
    parser.add_argument(
        "--sam3-scale-width",
        type=int,
        default=1280,
        help="Optional width for extracted clip frames. Set to 0 to keep the source width.",
    )
    parser.add_argument(
        "--sam3-propagate",
        action="store_true",
        help="After adding a prompt on the clip center frame, run short-window propagation and summarize the tracked outputs.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    require_local_gate()

    app_paths = build_app_paths(args.output_root)
    config = load_json_file(app_paths.config_path)
    profile = load_profile(Path(args.profile))
    linked_vods = load_linked_vod_map(app_paths.database_path)

    videos = resolve_video_candidates(
        args=args,
        config=config,
        linked_vods=linked_vods,
    )
    explicit_video_keys = {
        normalize_path(Path(raw_path).expanduser())
        for raw_path in args.video
    }
    jobs = build_video_jobs(
        videos=videos,
        linked_vods=linked_vods,
        profile=profile,
        ffprobe_bin=args.ffprobe_bin,
        preview_count=args.preview_count,
    )
    jobs = filter_jobs(jobs, explicit_video_keys, args.limit)

    summary = {
        "command": args.command,
        "backend": args.backend,
        "source": args.source,
        "analysis_root": str(app_paths.analysis_root),
        "config_path": str(app_paths.config_path),
        "database_path": str(app_paths.database_path),
        "configured_ascent_folder": config.get("ascent_folder", ""),
        "video_count": len(jobs),
        "videos": jobs,
    }

    if args.command == "probe":
        print(json.dumps(summary, indent=2))
        return 0

    run_dir = create_run_dir(app_paths.analysis_root, args.run_name)
    manifest = {
        "generated_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "command": args.command,
        "backend": args.backend,
        "source": args.source,
        "sam3_settings": {
            "window_seconds": args.sam3_window_seconds,
            "clip_fps": args.sam3_clip_fps,
            "scale_width": args.sam3_scale_width,
            "propagate": args.sam3_propagate,
        },
        "profile": profile,
        "config_path": str(app_paths.config_path),
        "database_path": str(app_paths.database_path),
        "configured_ascent_folder": config.get("ascent_folder", ""),
        "video_count": len(jobs),
        "videos": jobs,
    }
    write_json(run_dir / "manifest.json", manifest)

    if args.extract_previews:
        extract_previews(
            jobs=jobs,
            previews_root=run_dir / "previews",
            ffmpeg_bin=args.ffmpeg_bin,
        )

    if args.command == "plan" or args.backend == "dry-run":
        write_dry_run_results(jobs=jobs, results_root=run_dir / "results")
    else:
        write_sam3_results(
            jobs=jobs,
            profile=profile,
            results_root=run_dir / "results",
            clips_root=run_dir / "clips",
            ffmpeg_bin=args.ffmpeg_bin,
            window_seconds=args.sam3_window_seconds,
            clip_fps=args.sam3_clip_fps,
            scale_width=args.sam3_scale_width,
            propagate=args.sam3_propagate,
        )

    print(
        json.dumps(
            {
                "run_dir": str(run_dir),
                "manifest": str(run_dir / "manifest.json"),
                "video_count": len(jobs),
                "backend": args.backend,
            },
            indent=2,
        )
    )
    return 0


def require_local_gate() -> None:
    if os.environ.get(EXPERIMENT_FLAG) == "1":
        return

    raise SystemExit(
        f"Set {EXPERIMENT_FLAG}=1 before running this local-only experiment."
    )


def build_app_paths(output_root: str | None) -> AppPaths:
    local_app_data = os.environ.get("LOCALAPPDATA")
    if not local_app_data:
        raise SystemExit("LOCALAPPDATA is not set.")

    user_data_root = Path(local_app_data) / "LoLReviewData"
    analysis_root = Path(output_root) if output_root else user_data_root / DEFAULT_ANALYSIS_SUBDIR
    return AppPaths(
        user_data_root=user_data_root,
        config_path=user_data_root / "config.json",
        database_path=user_data_root / "lol_review.db",
        analysis_root=analysis_root,
    )


def load_json_file(path: Path) -> dict[str, Any]:
    if not path.exists():
        return {}

    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        raise SystemExit(f"Could not parse JSON file {path}: {exc}") from exc


def load_profile(path: Path) -> dict[str, Any]:
    data = load_json_file(path)
    prompts = data.get("prompts")
    if not isinstance(prompts, list) or not prompts:
        raise SystemExit(f"Prompt profile {path} does not contain a non-empty prompts array.")
    return data


def load_linked_vod_map(database_path: Path) -> dict[str, list[LinkedGameInfo]]:
    if not database_path.exists():
        return {}

    query = """
        SELECT
            vf.file_path,
            vf.game_id,
            g.timestamp,
            g.champion_name,
            g.position,
            g.win
        FROM vod_files vf
        LEFT JOIN games g ON g.game_id = vf.game_id
        WHERE vf.file_path IS NOT NULL AND vf.file_path != ''
    """

    mapping: dict[str, list[LinkedGameInfo]] = {}
    try:
        with sqlite3.connect(database_path) as connection:
            connection.row_factory = sqlite3.Row
            rows = connection.execute(query).fetchall()
    except sqlite3.DatabaseError as exc:
        raise SystemExit(f"Could not read linked VODs from {database_path}: {exc}") from exc

    for row in rows:
        file_path = row["file_path"]
        if not isinstance(file_path, str) or not file_path.strip():
            continue

        key = normalize_path(file_path)
        mapping.setdefault(key, []).append(
            LinkedGameInfo(
                game_id=int(row["game_id"]),
                timestamp=int(row["timestamp"]) if row["timestamp"] is not None else None,
                champion_name=row["champion_name"],
                position=row["position"],
                win=int(row["win"]) if row["win"] is not None else None,
            )
        )

    return mapping


def resolve_video_candidates(
    args: argparse.Namespace,
    config: dict[str, Any],
    linked_vods: dict[str, list[LinkedGameInfo]],
) -> list[Path]:
    candidates: dict[str, Path] = {}

    for raw_path in args.video:
        path = Path(raw_path).expanduser()
        if path.exists() and path.is_file() and path.suffix.lower() in VIDEO_EXTENSIONS:
            candidates[normalize_path(path)] = path

    if args.source in {"ascent", "all"}:
        ascent_folder = args.ascent_folder or config.get("ascent_folder")
        if isinstance(ascent_folder, str) and ascent_folder.strip():
            folder = Path(ascent_folder)
            if folder.exists():
                for path in discover_videos(folder):
                    candidates.setdefault(normalize_path(path), path)

    if args.source in {"linked", "all"}:
        for linked_path in linked_vods:
            path = Path(linked_path)
            if path.exists() and path.is_file():
                candidates.setdefault(normalize_path(path), path)

    ordered = sorted(
        candidates.values(),
        key=lambda path: path.stat().st_mtime,
        reverse=True,
    )
    if not ordered:
        raise SystemExit("No candidate videos found. Check your Ascent folder, linked VOD rows, or --video arguments.")

    return ordered


def discover_videos(root: Path) -> list[Path]:
    return sorted(
        (
            path
            for path in root.rglob("*")
            if path.is_file() and path.suffix.lower() in VIDEO_EXTENSIONS
        ),
        key=lambda path: path.stat().st_mtime,
        reverse=True,
    )


def build_video_jobs(
    videos: list[Path],
    linked_vods: dict[str, list[LinkedGameInfo]],
    profile: dict[str, Any],
    ffprobe_bin: str,
    preview_count: int,
) -> list[dict[str, Any]]:
    jobs: list[dict[str, Any]] = []
    for path in videos:
        metadata = probe_video(path, ffprobe_bin)
        sample_seconds = choose_sample_seconds(
            duration_seconds=metadata.get("duration_seconds", 0.0),
            profile=profile,
            preview_count=preview_count,
        )
        linked_games = [
            asdict(game_info)
            for game_info in linked_vods.get(normalize_path(path), [])
        ]
        jobs.append(
            {
                "name": path.name,
                "path": str(path),
                "size_bytes": path.stat().st_size,
                "modified_at_utc": dt.datetime.fromtimestamp(
                    path.stat().st_mtime,
                    tz=dt.timezone.utc,
                ).isoformat(),
                "probe": metadata,
                "sample_seconds": sample_seconds,
                "linked_games": linked_games,
                "unique_game_match": linked_games[0] if len(linked_games) == 1 else None,
            }
        )
    return jobs


def filter_jobs(
    jobs: list[dict[str, Any]],
    explicit_video_keys: set[str],
    limit: int,
) -> list[dict[str, Any]]:
    explicit_jobs: list[dict[str, Any]] = []
    discovered_jobs: list[dict[str, Any]] = []
    for job in jobs:
        key = normalize_path(job["path"])
        if not job["probe"].get("ok") and key not in explicit_video_keys:
            continue
        if key in explicit_video_keys:
            explicit_jobs.append(job)
        else:
            discovered_jobs.append(job)

    filtered = explicit_jobs + discovered_jobs

    if not filtered:
        raise SystemExit(
            "No probeable videos were found. If you want to inspect an incomplete file anyway, pass it explicitly with --video."
        )

    if limit > 0:
        filtered = filtered[:limit]

    return filtered


def probe_video(path: Path, ffprobe_bin: str) -> dict[str, Any]:
    command = [
        ffprobe_bin,
        "-v",
        "error",
        "-show_streams",
        "-show_format",
        "-print_format",
        "json",
        str(path),
    ]
    result = subprocess.run(
        command,
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        return {
            "ok": False,
            "error": result.stderr.strip() or result.stdout.strip() or f"{ffprobe_bin} failed",
        }

    try:
        payload = json.loads(result.stdout)
    except json.JSONDecodeError as exc:
        return {"ok": False, "error": f"Could not parse ffprobe output: {exc}"}

    streams = payload.get("streams", [])
    video_stream = next((stream for stream in streams if stream.get("codec_type") == "video"), {})
    format_info = payload.get("format", {})
    duration_raw = video_stream.get("duration") or format_info.get("duration") or 0
    fps_raw = video_stream.get("avg_frame_rate") or video_stream.get("r_frame_rate") or "0/1"

    return {
        "ok": True,
        "codec_name": video_stream.get("codec_name"),
        "width": coerce_int(video_stream.get("width")),
        "height": coerce_int(video_stream.get("height")),
        "duration_seconds": coerce_float(duration_raw),
        "fps": parse_frame_rate(fps_raw),
    }


def choose_sample_seconds(
    duration_seconds: float,
    profile: dict[str, Any],
    preview_count: int,
) -> list[int]:
    requested = profile.get("sample_seconds")
    valid: list[int] = []
    if isinstance(requested, list):
        for value in requested:
            numeric = coerce_float(value)
            if numeric is None:
                continue
            second = int(round(numeric))
            if 0 <= second <= max(int(duration_seconds) - 1, 0):
                valid.append(second)

    if valid:
        return dedupe_preserve_order(valid)

    if duration_seconds <= 0:
        return [0]

    if preview_count <= 1:
        return [max(int(duration_seconds // 2), 0)]

    start = min(45.0, duration_seconds * 0.1)
    end = max(start, duration_seconds - min(45.0, duration_seconds * 0.1))
    if end <= start:
        return [max(int(duration_seconds // 2), 0)]

    step = (end - start) / max(preview_count - 1, 1)
    samples = [int(round(start + index * step)) for index in range(preview_count)]
    return dedupe_preserve_order(samples)


def extract_previews(
    jobs: list[dict[str, Any]],
    previews_root: Path,
    ffmpeg_bin: str,
) -> None:
    for job in jobs:
        video_dir = previews_root / safe_slug(Path(job["name"]).stem)
        video_dir.mkdir(parents=True, exist_ok=True)
        for second in job["sample_seconds"]:
            output_path = video_dir / f"{int(second):04d}s.jpg"
            command = [
                ffmpeg_bin,
                "-y",
                "-ss",
                str(second),
                "-i",
                job["path"],
                "-frames:v",
                "1",
                "-q:v",
                "2",
                str(output_path),
            ]
            subprocess.run(command, capture_output=True, text=True, check=False)


def write_dry_run_results(jobs: list[dict[str, Any]], results_root: Path) -> None:
    results_root.mkdir(parents=True, exist_ok=True)
    for job in jobs:
        payload = {
            "status": "dry-run",
            "video": job["path"],
            "sample_seconds": job["sample_seconds"],
            "linked_games": job["linked_games"],
            "note": "No model inference was attempted.",
        }
        write_json(results_root / f"{safe_slug(Path(job['name']).stem)}.json", payload)


def extract_sam3_clip_frames(
    job: dict[str, Any],
    second: int,
    clips_root: Path,
    ffmpeg_bin: str,
    window_seconds: float,
    clip_fps: float,
    scale_width: int,
) -> dict[str, Any]:
    clip_dir = clips_root / safe_slug(Path(job["name"]).stem) / f"{int(second):04d}s"
    clip_dir.mkdir(parents=True, exist_ok=True)

    duration_seconds = coerce_float(job["probe"].get("duration_seconds")) or 0.0
    half_window = max(window_seconds / 2.0, 0.0)
    start_second = max(0.0, float(second) - half_window)
    end_second = float(second) + half_window
    if duration_seconds > 0:
        end_second = min(duration_seconds, end_second)
    clip_duration = max(end_second - start_second, 0.5)

    vf_filters = [f"fps={clip_fps:.6f}"]
    if scale_width > 0:
        vf_filters.append(f"scale={scale_width}:-2")

    command = [
        ffmpeg_bin,
        "-y",
        "-ss",
        f"{start_second:.3f}",
        "-t",
        f"{clip_duration:.3f}",
        "-i",
        job["path"],
        "-vf",
        ",".join(vf_filters),
        "-q:v",
        "2",
        str(clip_dir / "%05d.jpg"),
    ]
    result = subprocess.run(
        command,
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode != 0:
        return {
            "ok": False,
            "second": int(second),
            "error": result.stderr.strip() or result.stdout.strip() or "ffmpeg clip extraction failed",
        }

    frames = sorted(clip_dir.glob("*.jpg"))
    if not frames:
        return {
            "ok": False,
            "second": int(second),
            "error": "ffmpeg completed without writing any frames",
        }

    if clip_fps > 0:
        prompt_frame_index = int(round((float(second) - start_second) * clip_fps))
    else:
        prompt_frame_index = len(frames) // 2
    prompt_frame_index = max(0, min(prompt_frame_index, len(frames) - 1))

    return {
        "ok": True,
        "second": int(second),
        "resource_path": str(clip_dir),
        "clip_start_second": start_second,
        "clip_duration_seconds": clip_duration,
        "clip_fps": clip_fps,
        "frame_count": len(frames),
        "prompt_frame_index": prompt_frame_index,
        "prompt_frame_path": str(frames[prompt_frame_index]),
    }


def summarize_sam3_outputs(outputs: Any) -> Any:
    if not isinstance(outputs, dict):
        return summarize_structure(outputs)

    obj_ids = to_jsonable(outputs.get("out_obj_ids"))
    probs = to_jsonable(outputs.get("out_probs"))
    boxes = to_jsonable(outputs.get("out_boxes_xywh"))
    binary_masks = outputs.get("out_binary_masks")

    summary = {
        "object_count": len(obj_ids) if isinstance(obj_ids, list) else 0,
        "object_ids": obj_ids,
        "probs": probs,
        "boxes_xywh": boxes,
    }
    if binary_masks is not None:
        mask_shape = getattr(binary_masks, "shape", None)
        if mask_shape is not None:
            summary["mask_shape"] = list(mask_shape)
    if "frame_stats" in outputs and outputs["frame_stats"] is not None:
        summary["frame_stats"] = summarize_structure(outputs["frame_stats"])
    return summary


def write_sam3_results(
    jobs: list[dict[str, Any]],
    profile: dict[str, Any],
    results_root: Path,
    clips_root: Path,
    ffmpeg_bin: str,
    window_seconds: float,
    clip_fps: float,
    scale_width: int,
    propagate: bool,
) -> None:
    results_root.mkdir(parents=True, exist_ok=True)
    clips_root.mkdir(parents=True, exist_ok=True)

    try:
        from sam3.model_builder import build_sam3_video_predictor
    except ImportError as exc:
        raise SystemExit(
            "SAM3 backend requested, but the sam3 package is not importable. "
            "Install upstream SAM3 first, then retry."
        ) from exc

    predictor = build_sam3_video_predictor()
    prompts = profile["prompts"]

    for job in jobs:
        clip_payloads = {
            second: extract_sam3_clip_frames(
                job=job,
                second=second,
                clips_root=clips_root,
                ffmpeg_bin=ffmpeg_bin,
                window_seconds=window_seconds,
                clip_fps=clip_fps,
                scale_width=scale_width,
            )
            for second in job["sample_seconds"]
        }
        video_payload = {
            "status": "sam3-run",
            "video": job["path"],
            "linked_games": job["linked_games"],
            "clip_windows": list(clip_payloads.values()),
            "prompts": [],
        }

        for prompt in prompts:
            prompt_payload = {
                "id": prompt.get("id"),
                "text": prompt.get("text"),
                "bounding_boxes": prompt.get("bounding_boxes"),
                "bounding_box_labels": prompt.get("bounding_box_labels"),
                "points": prompt.get("points"),
                "point_labels": prompt.get("point_labels"),
                "obj_id": prompt.get("obj_id"),
                "samples": [],
            }

            for second in job["sample_seconds"]:
                clip_info = clip_payloads[second]
                if not clip_info.get("ok"):
                    prompt_payload["samples"].append(clip_info)
                    continue

                session_id = None
                try:
                    start_response = predictor.handle_request(
                        {
                            "type": "start_session",
                            "resource_path": clip_info["resource_path"],
                            "offload_video_to_cpu": True,
                        }
                    )
                    session_id = start_response.get("session_id")
                    if session_id is None:
                        prompt_payload["samples"].append(
                            {
                                "second": second,
                                "clip": clip_info,
                                "error": "start_session did not return a session_id",
                                "response": summarize_structure(start_response),
                            }
                        )
                        continue

                    frame_index = int(clip_info["prompt_frame_index"])
                    request = {
                        "type": "add_prompt",
                        "session_id": session_id,
                        "frame_index": frame_index,
                    }
                    if "text" in prompt:
                        request["text"] = prompt.get("text")
                    if "bounding_boxes" in prompt:
                        request["bounding_boxes"] = prompt.get("bounding_boxes")
                    if "bounding_box_labels" in prompt:
                        request["bounding_box_labels"] = prompt.get("bounding_box_labels")
                    if "points" in prompt:
                        request["points"] = prompt.get("points")
                    if "point_labels" in prompt:
                        request["point_labels"] = prompt.get("point_labels")
                    if "obj_id" in prompt:
                        request["obj_id"] = prompt.get("obj_id")

                    response = predictor.handle_request(request)
                    sample_payload = {
                        "second": second,
                        "clip": clip_info,
                        "frame_index": frame_index,
                        "response": {
                            "frame_index": response.get("frame_index"),
                            "outputs": summarize_sam3_outputs(response.get("outputs")),
                        },
                    }
                    if propagate:
                        propagation = []
                        for propagated_item in predictor.handle_stream_request(
                            {
                                "type": "propagate_in_video",
                                "session_id": session_id,
                                "start_frame_index": frame_index,
                                "propagation_direction": "both",
                            }
                        ):
                            if isinstance(propagated_item, dict):
                                propagated_frame_index = propagated_item.get("frame_index")
                                propagated_outputs = propagated_item.get("outputs")
                            else:
                                propagated_frame_index = None
                                propagated_outputs = propagated_item
                            propagation.append(
                                {
                                    "frame_index": propagated_frame_index,
                                    "outputs": summarize_sam3_outputs(propagated_outputs),
                                }
                            )
                        sample_payload["propagation"] = propagation
                    prompt_payload["samples"].append(sample_payload)
                except Exception as exc:  # pragma: no cover - depends on optional runtime
                    prompt_payload["samples"].append(
                        {
                            "second": second,
                            "clip": clip_info,
                            "error": repr(exc),
                        }
                    )
                finally:
                    if session_id is not None:
                        try:
                            predictor.handle_request(
                                {
                                    "type": "close_session",
                                    "session_id": session_id,
                                }
                            )
                        except Exception:
                            pass

            video_payload["prompts"].append(prompt_payload)

        write_json(results_root / f"{safe_slug(Path(job['name']).stem)}.json", video_payload)


def summarize_structure(value: Any, depth: int = 0) -> Any:
    if depth >= 4:
        return f"<{type(value).__name__}>"

    if value is None or isinstance(value, (str, int, float, bool)):
        return value

    if isinstance(value, dict):
        return {
            str(key): summarize_structure(item, depth + 1)
            for key, item in list(value.items())[:20]
        }

    if isinstance(value, (list, tuple)):
        items = [summarize_structure(item, depth + 1) for item in list(value)[:20]]
        if len(value) > 20:
            items.append(f"... {len(value) - 20} more")
        return items

    shape = getattr(value, "shape", None)
    if shape is not None:
        summary = {"type": type(value).__name__, "shape": list(shape)}
        dtype = getattr(value, "dtype", None)
        device = getattr(value, "device", None)
        if dtype is not None:
            summary["dtype"] = str(dtype)
        if device is not None:
            summary["device"] = str(device)
        numel = getattr(value, "numel", None)
        if callable(numel):
            try:
                summary["numel"] = int(numel())
            except Exception:
                pass
        return summary

    return repr(value)


def to_jsonable(value: Any) -> Any:
    if value is None:
        return None
    if isinstance(value, (str, int, float, bool)):
        return value
    if hasattr(value, "tolist"):
        try:
            return value.tolist()
        except Exception:
            pass
    if isinstance(value, dict):
        return {str(key): to_jsonable(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [to_jsonable(item) for item in value]
    return summarize_structure(value)


def create_run_dir(analysis_root: Path, run_name: str | None) -> Path:
    resolved_name = run_name or dt.datetime.now(dt.timezone.utc).strftime("%Y%m%d-%H%M%S")
    run_dir = analysis_root / resolved_name
    run_dir.mkdir(parents=True, exist_ok=False)
    return run_dir


def write_json(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def normalize_path(path: str | Path) -> str:
    return os.path.normcase(os.path.normpath(str(path)))


def parse_frame_rate(value: str) -> float:
    if not value or value == "0/0":
        return 0.0

    if "/" in value:
        numerator, denominator = value.split("/", 1)
        numerator_value = coerce_float(numerator)
        denominator_value = coerce_float(denominator)
        if not numerator_value or not denominator_value:
            return 0.0
        return numerator_value / denominator_value

    return coerce_float(value) or 0.0


def coerce_int(value: Any) -> int | None:
    if value is None:
        return None
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def coerce_float(value: Any) -> float | None:
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def dedupe_preserve_order(values: list[int]) -> list[int]:
    seen: set[int] = set()
    ordered: list[int] = []
    for value in values:
        if value in seen:
            continue
        seen.add(value)
        ordered.append(value)
    return ordered


def safe_slug(value: str) -> str:
    cleaned = "".join(character if character.isalnum() or character in {"-", "_"} else "-" for character in value)
    return cleaned.strip("-") or "video"


if __name__ == "__main__":
    sys.exit(main())
