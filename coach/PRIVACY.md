# Coach privacy

This document explains what data the coach touches, what leaves your
machine, and how you can control it.

## Data we touch

The coach reads your local SQLite database at
`%LOCALAPPDATA%\LoLReviewData\revu.db`. It reads:

- `games` — match stats and your review text (mistakes, went_well, focus_next, review_notes, spotted_problems)
- `session_log` — mental rating and improvement notes
- `matchup_notes` — your notes per champion-vs-enemy pairing
- `vod_files`, `vod_bookmarks` — VOD paths and clip metadata (NOT the video bytes, except for vision frame extraction when you explicitly request a clip review)
- `game_events`, `derived_event_instances`, `objectives`, `rules`, `concept_tags` — context

The coach writes its own tables only (allowlist-enforced in code):

- `game_summary` — compacted match summaries
- `review_concepts`, `user_concept_profile` — emergent vocabulary from your reviews
- `feature_values`, `user_signal_ranking` — per-user predictive features
- `clip_frame_descriptions` — structured descriptions of clip frames
- `coach_sessions`, `coach_response_edits` — coach drafts + your edits

## What leaves your machine

Depends on the provider you configure:

### Local provider (Ollama — default)

**Nothing leaves your machine.** The model runs locally. All requests go to
`http://localhost:11434`. The coach does not phone home.

### Hosted providers (Google AI Studio, OpenRouter)

When a hosted provider is active, the following leaves your machine:

- Compacted game summaries (structured JSON without VOD bytes)
- Your review text when extracting concepts
- Frame descriptions (not frame images, unless a hosted vision model is active)
- Prompts for coach-mode drafts

What does NOT leave your machine:

- Raw VOD video files
- API keys (stored in Windows Credential Manager, injected at runtime)
- Anything not explicitly sent as part of a coach request

You can generate frame descriptions locally via Ollama even when the
primary text provider is hosted: set `vision_override_provider: "ollama"`
in your coach config.

## Keys

API keys live in Windows Credential Manager, never in config files, log
files, or crash dumps. The sidecar process receives them via an
authenticated `POST /config` call from the app after startup, never via
environment variables or command-line arguments.

## Telemetry

Off by default. If you opt in via Settings, we collect only:

- Per-coach-call latency (milliseconds)
- Token counts (input/output)

We do NOT collect:

- Game content
- Review text
- Coach responses
- Any data that can be tied to your person

## Deletion

Uninstalling the coach from Settings removes the sidecar binaries from
`%LOCALAPPDATA%\LoLReviewData\coach\bin\`. Your data remains untouched;
you can wipe the coach-specific tables manually if you want:

```sql
DELETE FROM coach_response_edits;
DELETE FROM coach_sessions;
DELETE FROM clip_frame_descriptions;
DELETE FROM user_signal_ranking;
DELETE FROM feature_values;
DELETE FROM user_concept_profile;
DELETE FROM review_concepts;
DELETE FROM game_summary;
```

This is additive to the base app — removing coach data does not affect
game history, reviews, VODs, or any other core feature.
