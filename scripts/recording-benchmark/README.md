# Revu recording benchmark

These developer tools collect evidence. They never start capture, change game settings, request microphone/desktop access, kill existing applications, or install a required application. Revu remains one Overwolf Electron installation. Store raw output under the ignored `artifacts/recording-benchmark/` directory; it can include process identifiers and local machine details.

## Tools and collection

Use Node 22.12+, PowerShell 7, and Windows performance counters. PresentMon's console executable is an optional developer measurement tool; FFmpeg/ffprobe are optional media analysis tools. Record their exact versions and binary hashes, and obtain them from the upstream publisher. Missing tools, inaccessible counters, or missing game/Recorder access mean **unverified**, never zero. The scripts do not elevate privileges or change execution policy.

1. Record OS build, CPU, RAM, GPU and driver, SSD/model/free space, AC/battery state, power scheme, active display/adapter, game patch, display mode, game resolution, refresh rate, cap, VSync/VRR, graphics quality, Revu revision, runtime/Recorder/libobs version when exposed, encoder type/options, source/output dimensions, audio routes, container and background applications. `collect.ps1` saves the hardware subset. Runtime package approval must be established separately.
2. Start a repeatable League replay/practice sequence. Keep the exact game and camera sequence unchanged within each pair. Explicitly record whether another recorder is running; coordinate its state before testing, never silently terminate it. Begin capture only through Revu's authorized game-only Recorder path. Confirm no default audio, mic, monitor source, or overlay capture is selected.
3. In a terminal, run a separately obtained PresentMon console binary against the gameplay executable. Use a unique session name so another ETW session is preserved. Do not add `--restart_as_admin` or `--stop_existing_session`:

   ```powershell
   & $presentMonPath --process_name 'League of Legends.exe' --v1_metrics --timed 180 --terminate_after_timed --session_name RevuCaptureTrial01 --output_file 'C:\benchmark\trial01\presentmon.csv'
   ```

4. In another terminal, sample resources over the same interval. Supply the actual Revu main PID and any recorder helper root not parented beneath it. The collector discovers descendants every sample. For precise framing, align intervals by timestamp and use the same 10-second start trim in both modes; record recording start/stop UTC in a trial manifest. Counter sampling includes acquisition duration, so an overloaded sampler is visible.

   ```powershell
   ./scripts/recording-benchmark/collect.ps1 -OutputDirectory artifacts/recording-benchmark/trial01 -DurationSeconds 180 -IntervalMilliseconds 1000 -RootProcessIds 1234,5678 -Mode enabled
   node scripts/recording-benchmark/analyze.mjs --run artifacts/recording-benchmark/trial01 --presentmon 'C:\benchmark\trial01\presentmon.csv' --journal 'C:\probe-run\observations.jsonl' --start-seconds 10 --end-seconds 170
   node scripts/recording-benchmark/media.mjs 'C:\probe-run\capture.mp4'
   ```

   If multiple PIDs or swapchains appear, inspect their frame counts and supply `--process-id` and `--swap-chain` explicitly. The analyzer refuses to pool them. Use identical gameplay-only windows across disabled/enabled runs. PresentMon `MsBetweenPresents` measures application present cadence; the report also separates display interval statistics. Do not call either measure end-to-end input latency.

5. Repeat with recording disabled, keeping Revu, game, instrumentation and scenario unchanged. Use randomized ABBA blocks (disabled/enabled/enabled/disabled), at least three blocks per primary setting. Retain per-run results; compare paired deltas and report median, spread, and paired confidence intervals. Do not pool all frames across runs into one significance test or remove stutters as outliers. Instrumentation-only baseline checks must precede qualification: this CIM collector is diagnostic and its own CPU/WMI overhead can affect a CPU-bound game. Use a lower-overhead native/ETW collector for final qualification if overhead is material.

## Required scenarios and acceptance evidence

| Dimension | Cases |
|---|---|
| Recording mode | Disabled; Overwolf Recorder 720p30, 1080p30, 1080p60; current defaults and conservative tuning separately; any supported alternative at matched quality |
| Workload | Idle lane, movement/combat, repeatable teamfight; same scene capped with headroom and GPU-saturated/uncapped; UI visible and minimized |
| Hardware | Older low-core CPU + entry discrete GPU; NVIDIA, AMD, Intel/Arc; integrated/shared-memory GPU; hybrid laptop encoding on same and other adapter; battery and AC where supported |
| Storage | NVMe; SATA SSD; constrained free space; competing disk activity as a separate controlled condition |
| Reliability | Real full matches plus 60–120-minute soak; game exit and Alt-Tab; recorder/host crash on disposable outputs; delayed/full disk; immediate reopen/seek at several timestamps |

The collector supplies system CPU, available memory, owned process CPU normalized to whole-machine capacity, private bytes and working set, per-engine GPU activity, and physical disk read/write rates and queue depth. The raw process I/O counters include nondisk transfers: do not label them physical disk writes. GPU reporting sums processes on the **same** physical engine then takes the busiest engine within each engine type; it never adds separate engine percentages into total GPU usage. Keep per-adapter identities to expose cross-adapter copies. Working sets can double count shared pages when summed; private bytes are a separate commitment measure.

Frame metrics are mean, p50/p95/p99/p99.9 interval, average FPS, and **1% low = 1000 / mean of the slowest ceil(N × .01) frame intervals**. Record excursions over 16.67, 33.33 and 50 ms. Define the allowed regressions before results are viewed; a reasonable provisional decision gate is no reproducible >3% loss in 1% low, no >5% rise in p99 frame time, and no sustained frame-pacing spikes. These are project criteria, not established hardware guarantees.

Recorder journal deltas include skipped render/output frames and total frame counters when exposed. Counter resets invalidate the affected interval; skip counts from different stages must not be added as unique lost frames. First/last sampled counters exclude unsampled boundaries; take explicit start/stop snapshots for exact whole-match totals. Require no sustained active-FPS shortfall and report every nonzero skip counter with its denominator and coverage. A file frame count alone cannot identify missed source frames.

Measure stop-request to Recorder stop callback, registration/player-ready, and first successful seek separately using one monotonic clock. `journalSummary.stopToValidatedMs` currently measures the probe's stopping → finalized transition, including validation; it does not establish packaged-player readiness. Record bytes, duration, actual bitrate, finalization tail and whether interrupted output decodes/seeks. Neither process exit nor a successful read establishes physical persistence after power loss.

Inspect A/V content at the beginning, middle and end of each full match. Preselect repeatable game events with sharp visual and audible transitions; count frames and audio-sample onset, retain annotated timestamps and estimated uncertainty. Compare drift of the same event class rather than assuming its animation and sound inherently have zero offset. For calibrated absolute synchronization, use a known paired visual/audio test source through the same capture path or independently timestamped hardware reference. Container stream start/end offsets from `media.mjs` are only structural checks, and must remain `avSyncVerified: false` until content measurements exist. Proposed acceptance criteria are calibrated absolute offset ≤80 ms and drift ≤40 ms per hour, with measurement uncertainty adequate to resolve those limits. These are project proposals, not established recorder guarantees.

## Synthetic experiment

```powershell
node scripts/recording-benchmark/synthetic.mjs --output artifacts/recording-benchmark/synthetic-new --seconds 15
node scripts/recording-benchmark/av-fixture.mjs
node --test scripts/recording-benchmark/analyze.test.mjs
```

This generates an artificial 1080p60 CPU test pattern and sine. Disabled output goes to the null muxer; enabled output uses NVENC H.264/AAC and fragmented MP4 at 720p30/4 Mb/s, 1080p30/6 Mb/s and 1080p60/10 Mb/s. An AMF initialization trial and forced termination of **only the FFmpeg process created by this experiment** compare ordinary/fragmented MP4 structural recovery. Graceful stop measures `q` request to FFmpeg exit, including drain/muxing/input response. Each run saves exact arguments, logs, hardware inventory, counter samples and output files, with no overwrite. The fixed experiment bitrates are independently labeled and are not automatically synchronized to Revu presets.

CPU-generated frames require upload and lack the game's GPU contention/capture hook. FFmpeg's encoder/muxer behavior is not asserted identical to the Overwolf/libobs implementation. This experiment can expose local encoder availability and file-structure behavior; it cannot establish League FPS impact, Overwolf access, game-only capture correctness, or full-match synchronization.

`av-fixture.mjs` separately measures generated flash/beep events before and after 720p30 NVENC/AAC encoding, calibrating against the lossless source and testing a known +120 ms audio-delay positive control. Its content-alignment result applies only to the synthetic codec/mux path. It does not test Overwolf, audio devices, game capture or long-match drift.

## Primary method references

- Intel/GameTechDev, [PresentMon console documentation](https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md): process/swapchain filtering, CSV metric definitions, timing and ETW session flags. Accessed September 12, 2026.
- Overwolf, [RecorderStats](https://dev.overwolf.com/ow-electron/reference/Overwolf-electron-APIs/recorder/interfaces/RecorderStats/): render/output totals, skipped counters, active FPS, CPU, memory and disk-space fields. Accessed September 12, 2026.
- FFmpeg, [format documentation: fragmentation](https://ffmpeg.org/ffmpeg-formats.html#Fragmentation): ordinary MP4 versus fragmented output behavior. Verify the actual runtime muxer's output separately.
