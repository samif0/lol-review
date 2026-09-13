# Overwolf Electron capability probes

This directory prepares the developer-key test path. It is a separate development package with an actual Overwolf Electron startup check, a GEP diagnostic and a manual Recorder diagnostic. It does not connect to Revu's database or attach recordings to reviews. **Live package access and real League recording must still be verified after the key arrives.** The integrated automatic Revu workflow is documented in [Automatic match recording](../electron/RECORDING.md).

## Reproduce the checks without gaming packages

Install shared dependencies from `desktop/`, using Node 22.12 or newer. This
directory is an npm workspace; it has no separate dependency tree or lockfile:

```powershell
npm ci --ignore-scripts
cd overwolf-probes
npm run check
npm test
npm run setup
npm run readiness
npm run preflight
```

The exact runtime package is `@overwolf/ow-electron@42.7.1`; package types are `@overwolf/ow-electron-packages-types@1.1.11`. `setup` installs the pinned binary using its official installer, verifies its executed version and SHA256, creates a pending metadata template without overwriting existing values, and prepares a self-contained scratch app. Downloading the runtime does not establish package approval.

`readiness` launches that actual runtime with `overwolf.packages: []` and removes inherited gaming credentials from the child environment. It verifies app startup, renderer loading and sandbox isolation, records the observed application UID, exits the owned process and verifies lock cleanup. A passing `readiness-result.json` explicitly says `liveAccessVerified: false`. This check requires no developer key. `prepare:probe` only builds and prepares the scratch app; `runtime:install` installs/verifies only the runtime. No live gaming startup belongs in CI.

`preflight` reports credential **presence**, credential precedence, actual runtime version and hash, and the existence of a League gameplay process. It never prints credential values. A partial Console credential pair is reported because it can shadow a developer key. The report cannot prove remote approval. A League launcher process is insufficient.

## Access review before a live diagnostic

Approved developer status and valid development credentials enable the gaming packages under the vendor's Dev Mode contract; no separate Recorder developer key is documented. The package flags, evidence reference and seven-day verification date below are **Revu's local diagnostic checks**, not separate Overwolf approvals or an access-grant mechanism. For the integrated app's simpler development launch, see [Automatic match recording](../electron/RECORDING.md).

Confirm the proposal covers integrated recording, approved developer status, the assigned application identity, the selected package and League access, and the intended distribution route. Store actual credentials only in the approved environment using the vendor's [Dev Mode instructions](https://dev.overwolf.com/ow-electron/guides/dev-tools/dev-mode/). Never put keys in this package, `access.json`, a screenshot, logs, or source control.

When the key arrives, fill the ignored `.local/access.json` created by setup with **metadata only**, reflecting your approved application and package access. These pending values deliberately do not pass the launch gate:

```json
{
  "status": "pending",
  "verifiedAt": "",
  "productName": "",
  "authorName": "",
  "appId": "",
  "evidenceReference": "",
  "leaguePatch": "",
  "leagueAccess": false,
  "gepAccess": false,
  "recorderAccess": false,
  "capture": {
    "sourceWidth": 1920,
    "sourceHeight": 1080,
    "preset": "720p30",
    "allowSoftwareEncoder": false
  }
}
```

Use the product and author names declared for your approved application. After entering those two values, run `npm run readiness` again to observe the derived runtime `uid`; enter that value as `appId`. Observing a UID does not establish approval. The live runtime must match it before capture. `verifiedAt` must be a valid UTC timestamp within seven days. The reference identifies where a human reviewed approval; it is neither a credential nor automatic verification. Check the gameplay resolution locally and update the source dimensions before every capture; automatic resolution-change tracking is not implemented. Source dimensions cannot safely be inferred from the monitor.

Use the masked, session-only prompt to launch each diagnostic. It temporarily selects `OW_DEV_KEY` over any Console credentials and restores the prior environment afterward. Paste the key into the local prompt, never into this file or chat:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-ApprovedProbe.ps1 -Mode gep
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-ApprovedProbe.ps1 -Mode recorder
```

`-ExecutionPolicy Bypass` applies only to this PowerShell process; it does not change the machine policy. If credentials are already configured in the launching process, the equivalent commands are:

```powershell
npm run probe:gep -- --enable-live
npm run probe:recorder -- --enable-live
```

Each command loads only its named package in a generated scratch application. An unexpected package prevents capture until its dependency is understood. No Overlay, Native client, or installed app is added or modified. If a documented runtime dependency becomes required, review it and update the narrow package list explicitly.

The Recorder window has a native **Probe** menu. When a genuine League gameplay process is detected, choose **Start game-only capture (microphone off)**, then **Stop capture**. Starting the executable alone never starts recording. Each run permits one manually selected session; rerun for a second match. Game exit requests stop, while Recorder's own game-exit stop is a backstop. GEP is a separate process/package and cannot block Recorder operation.

## What gets recorded

- Recorder: one continuous fragmented MP4, explicit H.264 and AAC, League gameplay PID, cursor enabled, overlays disabled, no desktop fallback. All default audio sources, microphone inputs, and system outputs must be absent; one League application audio source is required. Software x264 needs explicit opt-in. Hardware encoder requests are verified against the built settings, but actual hardware activity still needs measurement.
- Resolution: configured source resolution is retained; output is downscaled to at most 720p30, 1080p30 or 1080p60 without upscaling. Initial CBR video targets are 5, 8 and 12 Mbps respectively, not a measured quality recommendation. All use two-second keyframes and SDR NV12/Rec.709 limited range. Game capture is explicitly frame-limited with shared-memory compatibility disabled.
- Encoder policy: a supported hardware H.264 provider default is preferred, with NVENC/AMF/QSV fallback order only when needed. This does not verify adapter affinity. The optional `capture.encoder` metadata field selects exactly `obs_nvenc_h264_tex`, `h264_texture_amf`, `obs_qsv11_v2` or `obs_x264`; an unavailable choice fails, and x264 still requires `allowSoftwareEncoder: true`. NVENC uses P3/single pass/lookahead off/AQ off, AMF speed, QSV TU6/enhancements off, and x264 ultrafast. Contradictory reported properties or changed built settings prevent start. Native acceptance and performance remain live-test requirements.
- Audio: one application source, stereo 48 kHz, normal buffering; no low-latency audio mode or filters. AAC bitrate is a backend setting to measure, not a requested portable field.
- GEP: `matchState`, `counters`, `damage`, `abilities`, `announcer`. Only whitelisted numeric values and enum values are serialized, after a supported queue ID arrives. Queue IDs 0/400/420/430/440 are the initial test scope; all other and unknown queues are withheld. No chat, match IDs, player names, command lines, raw errors, or full state dumps enter application journals. Selection/key-press events are not promoted to activations.
- Timing: UTC and monotonic receipts, observed game clock, lifecycle transitions, stop start-time/duration where provided. Receipt timestamps are not exact event onset or first-frame timing.

Output is under `.local/runs/<mode>-<random-run-id>`. Each generated app contains its own compiled modules, renderer and relative entry point. Application journals stop adding rows at 20,000 rows or 4 MiB, with a 2 KiB per-row maximum. Recorder statistics are sampled at most once per second, plus one terminal sample when available; render/output totals, skipped counts, render time and free disk are retained, and missing metrics are null. `recorder-stop.requestToCallbackMs` measures an explicit stop request to callback; automatic exits have no request timestamp. Recorder FPS is not League FPS.

Capture output size and free space are checked on the one-second tick using nonoverlapping asynchronous metadata reads; stalled storage requests stop after three seconds. Stop is requested after two hours, above 20 GiB, or below a 2 GiB reserve. These diagnostic limits can end capture before the match; `finalized` does not establish full-match coverage. Initial space admission and session intent persistence remain synchronous. A tracked game uses cheap PID liveness checks; executable discovery uses PowerShell only when no game is tracked. These limits do not bound Overwolf's own logs, caches, telemetry, or a hung native provider. No automatic pruning is implemented. Keep outputs local; on-screen game chat can appear in video.

Session intent is persisted before capture. A completed callback, successful media probe and three decoded samples are required for diagnostic `finalized` state. This state does not establish packaged-player compatibility, full-match integrity, correct game association, A/V drift, or review latency. Failures/segments/crashes stay partial and their files are preserved. Main-process exceptions are represented as fixed error categories, never raw payload dumps.

## Recovery and known limits

A per-mode lock records the launch token, launcher PID, runtime PID and run directory. Closing the launcher requests graceful shutdown; the runtime also checks parent liveness. A lock stays in place if native capture may still be active or persistence failed. Use the recovery utility below for an interrupted launcher. Never terminate unrelated processes. The probes do not repair or adopt old media or continue reconnect segments.

Start GEP before launching League gameplay: the installed API enables games through detection events and does not enumerate already-detected games. `getInfo()` has no verified Electron snapshot envelope, so only presence is logged and a fresh supported queue update is required. **Retry game events** retries registration for an actually detected game. Errors, privilege mismatch, exit and clock resets invalidate prior coverage; subscriptions are disposed on shutdown.

Recorder setup, start, stop, completion and validation have bounded waits. A stop can cancel a pending setup, late callbacks cannot rewrite a terminal state, and failed storage does not recurse through error logging. The local process scan supports Recorder late attachment using only the exact gameplay executable and PID; it does not manufacture a GEP detection. Unresolved native stop remains marked as possibly active. Real game capture, audio isolation, hardware encoder behavior, app/OBS lifetime after forced shutdown, low-disk timing and reconnect behavior still require controlled live tests.

The first live sequence is: verify package activation and UID; run GEP before a practice game; run one manually started/stopped Recorder session; play/seek its file and check game-only audio; then exercise game exit and interrupted capture. `finalized` proves only the diagnostic's metadata/media checks. Production match association, automatic start/end, 15-second review readiness, retention and signed distribution are still open.

Use the [capability and acceptance record](../../docs/plans/windows-handoff/overwolf-capabilities.md) to record measurements and gates. Do not mark fixtures as hardware proof.

### Recover an interrupted launcher

An actual no-key readiness test that forcibly ended its launcher also observed the runtime exit, but its lock remained because orderly JavaScript cleanup did not complete. Forced shutdown does not guarantee graceful cleanup. After inspecting the indicated run directory, recover the relevant mode with:

```powershell
node scripts/recover-lock.mjs readiness
node scripts/recover-lock.mjs gep
node scripts/recover-lock.mjs recorder
```

Run only the command for the interrupted mode. The utility never terminates processes. It removes a lock only when both recorded launcher and runtime PIDs are confirmed dead and the lock still belongs to the same run. Live, inaccessible, missing or ambiguous PID records remain blocked. Recorder additionally requires a saved healthy `run-result.json`, `captureMayBeActive: false`, and no active session state. Keep ambiguous locks and partial media for investigation; deleting them cannot establish that native recording stopped.
