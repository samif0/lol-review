# Practice diagnostics

Opt-in via `scripts/Practice-Capture.ps1 start`; status and stop use the same script.
No new UI, game-event writes, ranked-history writes, recorder work, or AI.
Authenticated local endpoints live under `/api/diagnostics/practice`.

The armed session waits up to one hour for the next local Live Client game. Enter
Practice Tool intentionally; the sampler is independent of ranked-review queue gates.
It captures `/liveclientdata/allgamedata` once per second, stops at 900 samples,
15 game minutes, 32 MB, clock reset, or ten consecutive request failures after
capture starts. Stop explicitly before switching games. Arming twice is idempotent.
Restarting the app disarms it. A forced app termination may leave an incomplete log.

Raw local snapshots (including player names returned by Riot), receipt times and
request durations are retained in `%LOCALAPPDATA%/Revu/diagnostics/practice-*`.
No credentials or remote uploads are involved. Explicit source gaps appear in the
JSONL file. The final report is produced by the production `ProcessingSession`
engine with a registered diagnostic health-change detector in shadow mode. Every
change references its before/after observations. It is never a TRADE event.

`captured-prefix-report.json` separately replays observations up to the first source
gap, preserving diagnostic facts when quitting disconnects the API. Its scope ends
at that gap or the last sample; it never bridges a dropout or changes the incomplete
full-session report. It remains shadow-only and cannot publish timeline markers.

Suggested controlled sequence: stand still; attack a dummy; take damage from
minions or a turret; cast Barrier while taking damage; disengage. Note the game
clock for each action. An enemy-bot exchange, where available, is a separate test
from a dummy hit. Dummy damage is not evidence of a real champion exchange.

This instrument validates capture/clock/engine behavior. It does not connect an
Overwolf source, establish damage attribution, or prove that cast timestamps are
available. Raw schema and labeled action windows must be examined before any
advanced detector is enabled. CPU figures cover the whole sidecar, not isolated
added overhead; gameplay frame-time and matched resource gates remain unmeasured.
