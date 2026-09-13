# Automatic match recording

Revu owns the recording workflow and calls Overwolf Recorder for native game/audio capture and hardware encoding. The sidecar supplies exact match identity and links completed media through the existing VOD repository. No separate Overwolf client or recording application is required.

Ascent folder imports are available temporarily while built-in recording is unreleased. Connect the folder in Settings → Recording → Ascent recordings, then scan for existing videos. Revu checks again on startup and after matches. Matching uses filename timestamps and last-write times across the latest 100 games; ambiguous or unfinished files stay unlinked. Existing VODs and pending native recording claims take precedence. Disconnecting preserves linked videos and their files.

## User behavior

Settings → Recording marks built-in recording **Coming soon**. Its toggle, quality selector, folder button and save button are disabled until the product feature is released, independently of native Recorder availability. Ascent imports remain usable. Settings → Appearance & startup offers close to system tray and start with Windows, both initially off. Startup is available only in an installed Windows app with the stable Velopack launcher; it is not applied in a developer or portable build.

The following native capture behavior describes the implementation under development; it is not an available recording option in the current Settings UI.

Revu must be running before gameplay starts for full coverage. Recorder initialization supplies game launch/exit notifications with exact-PID discovery as a late-attachment fallback. The native adapter selects only League video and application audio, hardware H.264/AAC, fragmented MP4, frame-limited capture and the researched presets. A temporary not-yet-created game window is retried before entering native capture.

Recording continues until native game exit or the sidecar confirms that exact match ended. Game exit alone does not prove a completed match. Reconnects retain separate partial segments; late attachment, unknown match identity, missing clock anchors, failed validation and interrupted shutdown remain partial. Existing VOD links are never overwritten. Files remain under `%LOCALAPPDATA%/Revu/Recordings/<session-id>/`; each session has durable metadata. The folder button exposes retained partial media. No automatic pruning deletes recordings.

Completion also requires footage covering the last observed game clock, received within 10 seconds before the exact match-end confirmation. The initial coverage tolerance is 2 seconds; it is a provisional allowance for clock sampling, not measured synchronization accuracy. A successful native stop callback alone cannot turn an early-ended video into a full match. Missing or stale final-clock evidence retains the file as partial.

The sidecar retries attachment when a match row arrives after the video. Session IDs make registration idempotent, including a lost response. Capture start and an observed game clock establish a time offset saved beside linked videos. Playback, bookmarks, corrections and clip extraction use game time; the media boundary applies the offset. This observed-clock estimate is not a measured first-frame timestamp or validated live synchronization result.

## Before developer approval

The ordinary build declares no gaming packages and shows built-in recording as Coming soon. Ascent imports need no developer key. Native provider tests exercise the implementation but do not establish live League/Vanguard compatibility, native performance, game-audio isolation or full-match A/V drift. Loading Recorder for development does not remove the separate product-release gate on Settings.

After developer status is approved, generate your Developer Key in the Overwolf profile. Valid development credentials enable Recorder; there is no separate local Recorder key documented. The older probe's `recorderAccess`, `leagueAccess`, and seven-day metadata checks are Revu's own diagnostic checklist, not vendor entitlement grants.

Close the existing Revu instance, build the sidecar, and launch the integrated development app:

```powershell
cd desktop
npm run build:sidecar
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-RecorderDevelopment.ps1
```

The masked prompt keeps the key in the child process environment for this launch and restores the previous environment afterward. Product/author identity comes from `overwolf-probes/.local/access.json` (metadata only). The launcher copies the actual Revu host/UI into ignored `artifacts/recorder-development`, declares only Recorder, and uses the normal Revu data/profile and single-instance lock. It does not mutate the source manifest or publish a build. With credentials already in the launching environment, `npm run start:recorder` is equivalent.

In Settings enable automatic recording, start a League Practice Tool game, and check Recording status. End the game, wait for saving, then open the recordings folder. Practice Tool may have no persisted reviewable match row; a retained pending match receipt is expected in that case. Use a reviewable full match to verify automatic attachment. Compare recording disabled/enabled using `scripts/recording-benchmark/README.md` before making a performance claim.

## Checks and remaining validation

`npm test` covers the adapter with fake native APIs, lifecycle cancellation/reconnects/duplicates/storage errors, background preferences and timeline conversion. Core/sidecar tests cover exact match identity, durable registration, conflicting paths, delayed match rows and clip timing. `scripts/Test-ElectronHost.ps1` runs the real credential-free host, Settings and synthetic H.264 playback/seek in scratch storage. No test changes this machine's Windows startup registration.

Verified after external recorder integration removal on 2026-09-12: 870 Core, 223 Sidecar, 137 desktop and 119 probe tests passed, including the optional synthetic media tests. The desktop run included real FFmpeg 8.1 fixtures with video or audio ending early, which the media validator rejected. `npm ci --ignore-scripts`, the UI build and probe type check succeeded. Overwolf Electron 42.7.1 passed the real isolated Settings/H.264/clock-seek smoke and stopped its owned sidecar. The host smoke also confirms retired recorder controls are absent and native controls remain. Local smoke evidence: `artifacts/overwolf-handoff/smoke-731585b6f7f74ee981ef371d62661f58/electron-smoke.json`. Media validation samples both streams near the start, midpoint and end; it does not prove every frame, semantic A/V alignment or drift.

To include the optional real media tests when repeating `npm test`, set `REVU_FIXTURE_FFMPEG` and `REVU_FIXTURE_FFPROBE` to absolute paths of existing local binaries. These are developer test dependencies; normal recording validation uses the tools supplied by Recorder.

Still unverified: activated Recorder in real League, hybrid/low-end hardware, runtime child lifetime after forced host exit, in-match resolution changes, full-match game-clock drift, actual tray/logoff behavior and installed Windows startup. Client-rectangle dimensions are not proof of League's internal render resolution. A single initial capture-size snapshot is used; resolution-change handling needs live validation. A 2 GiB disk reserve requests a safe stop; disk stalls/native failures can still leave partial footage. Full native frame counters are retained from the stop callback when supplied; they are not League frame-time measurements.

A recording-enabled distributed build additionally needs the documented Overwolf integrity signing and executable code signing. The existing unsigned package command deliberately keeps gaming packages disabled until that release setup and live acceptance are complete. See [Dev Mode](https://dev.overwolf.com/ow-electron/guides/dev-tools/dev-mode/) and [production signing](https://dev.overwolf.com/ow-electron/guides/dev-tools/app-signing/).
