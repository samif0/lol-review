"""Read-only comparison of retained Live Client events and an exported LCU timeline.

This measures agreement between feeds, not gameplay ground truth. It deliberately
does not interpret kill-credit participants as fight membership or damage exchanges.
"""
import argparse
import json
import sqlite3
from collections import Counter
from pathlib import Path


def audit(database, timeline_path, game_id, self_id):
    timeline = json.loads(Path(timeline_path).read_text(encoding="utf-8-sig"))
    frames = timeline["frames"]
    source_events = [event for frame in frames for event in frame["events"]]
    expected = []
    for event in source_events:
        if event["type"] != "CHAMPION_KILL":
            continue
        kind = ("KILL" if event["killerId"] == self_id else
                "DEATH" if event["victimId"] == self_id else
                "ASSIST" if self_id in event.get("assistingParticipantIds", []) else None)
        if kind:
            expected.append((kind, event["timestamp"] / 1000))
    with sqlite3.connect(Path(database).resolve().as_uri() + "?mode=ro", uri=True) as conn:
        rows = conn.execute(
            "SELECT event_type,game_time_s FROM game_events WHERE game_id=? ORDER BY game_time_s",
            (game_id,)).fetchall()
        processing = conn.execute(
            "SELECT report_json FROM event_processing_reports WHERE game_id=?", (game_id,)).fetchone()
    unmatched = [(kind, time) for kind, time in rows if kind in ("KILL", "DEATH", "ASSIST")]
    pairs = []
    missing = []
    for kind, source_time in sorted(expected, key=lambda item: item[1]):
        eligible = [(i, abs(time - source_time)) for i, (k, time) in enumerate(unmatched)
                    if k == kind and abs(time - source_time) <= 1]
        if not eligible:
            missing.append({"kind": kind, "source_seconds": source_time})
            continue
        # Keep chronological order. Nearest-neighbor matching can steal the next
        # event when two same-kind takedowns occur less than a second apart.
        index, error = eligible[0]
        _, live_time = unmatched.pop(index)
        pairs.append({"kind": kind, "live_seconds": live_time,
                      "lcu_seconds": source_time, "absolute_difference_seconds": round(error, 3)})
    return {
        "version": 1, "game_id": game_id, "self_participant_id": self_id,
        "measurement": "Cross-source timestamp agreement, not ground-truth accuracy",
        "stored_counts": dict(Counter(kind for kind, _ in rows)),
        "lcu_frame_count": len(frames),
        "lcu_intervals_ms": sorted(set(b["timestamp"] - a["timestamp"] for a, b in zip(frames, frames[1:]))),
        "lcu_event_counts": dict(Counter(event["type"] for event in source_events)),
        "participant_fields": sorted({key for frame in frames for value in frame["participantFrames"].values() for key in value}),
        "kill_event_fields": sorted({key for event in source_events if event["type"] == "CHAMPION_KILL" for key in event}),
        "expected_own_combat_events": len(expected), "matched_within_one_second": len(pairs),
        "missing_live_events": missing, "unmatched_live_events": unmatched, "pairs": pairs,
        "processing_report": json.loads(processing[0]) if processing else None,
        "advanced_detector_accuracy": "not measured: required observations unavailable",
        "gameplay_resource_overhead": "not measured",
        "end_to_end_review_latency": "not measured",
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", required=True)
    parser.add_argument("--timeline", required=True)
    parser.add_argument("--game-id", required=True, type=int)
    parser.add_argument("--self-participant-id", required=True, type=int)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    report = audit(args.database, args.timeline, args.game_id, args.self_participant_id)
    Path(args.output).write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({key: report[key] for key in (
        "game_id", "expected_own_combat_events", "matched_within_one_second",
        "missing_live_events", "unmatched_live_events")}))
