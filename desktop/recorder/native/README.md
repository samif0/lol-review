# Native recording helper

`revu-recorder.exe` links only against the pinned OBS 32.2.2 core. It runs next
to `obs.dll` in the prepared recorder bundle; it does not use installed OBS
settings, plugins, or the OBS Studio interface. Build through the recorder setup
script so the verified source headers and DLL import library agree.

## Protocol 1

The host launches one short-lived process for `--probe` or `--discover`, and one
process per recording:

```text
revu-recorder.exe --record --pid 1234 --output C:\recordings\session\capture.mp4 --preset 720p30 --priority below-normal
```

The only presets are `720p30`, `1080p30`, and `1080p60`. `--priority` is
`below-normal` (the default when omitted) or `normal`. JSON lines on stdout
are protocol messages; diagnostics use stderr. `--probe` reports capabilities,
and `--discover` reports live League game process IDs and available client dimensions.
A loading or minimized game remains present with zero dimensions. Discovery uses
a process snapshot, window enumeration and limited process queries, with no shell,
WMI, process memory reads, or graphics initialization. The capability response's
`encoders` array contains approved registered H.264 hardware candidates for the
active Direct3D render adapter. `selectedEncoder` is one of those IDs when capture
is available, or `null` with an unavailability `code`. `adapterVendor`, its PCI
vendor ID and its LUID identify that render adapter; they do not claim that every
GPU installed in the PC is an eligible recording target. Driver initialization
is confirmed when the selected encoder produces actual media at recording start.

During a recording, send `{"command":"stop"}` followed by a newline to stdin.
Input EOF and game process exit also stop the recording. The helper emits
`started`, periodic `heartbeat`, then `stopped`. A heartbeat demonstrates the
helper's liveness; `frames` is a separate encoded-video packet counter. `started`
confirms the requested `priority` and reports the GPU scheduling class actually in
effect as `gpuPriority`. Missing capabilities and failures emit `error` and are never
silently replaced with software encoding, display capture, or game hooks.

`stopped.stats` adds `repeatedFrames` and `perMinute`, three equal-length arrays
indexed by movie minute (at most 300): `frames` encoded, `repeated` frames held by
a capture stall, and `lagged` render ticks libobs missed after the output started.
A repeated frame is one or two frames encoded below 20% of the per-frame bit budget
between frames at or above 40% of it; longer runs are still screens and are not
counted. This is an estimate calibrated on 1080p60 NVENC against decoded frames.

## Recording contract

- Explicit WGC window capture, including the game cursor, with SDR output. A
  small OBS source wraps the pinned `libobs-winrt.dll` capture API and binds the
  selected HWND/PID for its lifetime. Capture starts only after the first actual
  WGC texture arrives; there is no window-title search or target reacquisition.
- Separate League application audio source; no microphone or desktop mix.
- Automatic NVIDIA NVENC, AMD AMF or Intel QSV H.264 hardware compression based
  on registered capabilities and the active render-adapter vendor. All use CBR,
  high profile, two-second keyframes and zero B-frames. NVIDIA retains preset P4
  with lookahead and multipass disabled; AMD uses speed with pre-analysis off;
  Intel uses TU6 and ultra-low latency to avoid its normal-mode lookahead. AAC
  is 48 kHz stereo at 160 kbit/s.
- Revu selects no encoder on another render-adapter vendor and performs no engine
  restart or candidate retry during a game. OBS may internally use frame input
  within the same hardware backend if texture sharing fails. That remains
  hardware compression and can add transfer work; the public API does not expose
  its internal rerouted ID. There is no CPU software-encoding fallback.
- FFmpeg/AAC/muxing and application audio modules are required. Vendor-specific
  NVENC and QSV modules are optional independently; an unavailable vendor module
  does not block another compatible backend. Only the active adapter's NVIDIA
  or Intel module is loaded, avoiding unrelated hardware-probe timeouts. AMD
  capability detection is part of the required FFmpeg module. The selected hardware can still
  fail to initialize, in which case the helper reports the failure and stops.
- MP4 is fragmented while recording, with half-second fragments and two-second
  keyframes, and finalized to a normal indexed MP4 on a clean stop. The muxer's
  hybrid fragmented mode appends a complete moov at the end and only then turns
  its start placeholder into one mdat covering the earlier fragments, so players
  open the file without walking every fragment. There is no remux or copy. An
  interrupted file keeps the fragmented layout and plays up to its last complete
  fragment; the host never validates it. If a finished file's index never reached
  the disk, the host restores the fragmented layout. Interrupted trailing data still
  needs separate validation and is never claimed complete. The stock OBS muxer
  holds a final IO chunk in memory; recovery can require trimming an incomplete
  trailing fragment. Revu preserves the original partial file and never silently
  promotes it to complete.
- The game always comes first. By default the helper and its muxer child run at
  below-normal CPU priority and the helper at below-normal GPU scheduling priority;
  `--priority normal` raises both to normal and never above. libobs raises its GPU
  scheduling class to HIGH (REALTIME without hardware-accelerated GPU scheduling)
  while creating its device, which Windows permits without administrator rights
  even though libobs then logs that its GPU priority setup failed. The helper pins
  the class right after video initialization, resets the device GPU thread
  priority, and re-checks every second; if it cannot stay at or below normal, the
  recording stops with `gpu-priority-unavailable`.
- Windows power throttling is disabled for the helper. Otherwise Windows 11 can
  ignore the 1 ms timer resolution libobs requests for this windowless process,
  waking the 60 FPS render loop up to 15 ms late and repeating frames. This keeps
  timers and clock speed normal without raising scheduling priority.
- 2 GiB process / 3 GiB process-job commit ceilings. The non-inheritable job handle
  stays open for the helper lifetime; helper termination kills the FFmpeg muxer child as well.
- Startup is bounded to 30 seconds and stop/cleanup to 6 seconds. This includes
  unresponsive OBS modules; the watchdog can terminate the isolated helper.
- The bundle omits the stock `win-capture` module, game hook, graphics-offset and
  injection helper assets. The wrapper has no compatibility updater, capture
  injection code, BitBlt fallback, or display-capture source.

`startedAt` is observed from the first encoded video packet's OBS composition
timestamp, minus its final interleaved PTS, calibrated against precise Windows
UTC. The helper refuses to start without that packet timing and a clock-read
uncertainty below 1 ms. This verifies the **movie composition timeline**; it does
not assert that WGC's underlying frame has zero capture latency. Negative
timestamp shifting is disabled in the muxer and B-frames are disabled so the
reported origin maps to video PTS zero.

Capability probing and compilation do not establish League gameplay performance,
cursor latency, Vanguard acceptance, long-run audio sync, or forced-kill recovery.
Those require the separately gated live acceptance run before release.

CTest's `hardware-encoder-policy` uses fake registered metadata to verify all
three vendors, mixed adapters, deterministic selection, missing modules/hardware,
unknown/software/internal encoders and the vendor-specific recording settings.
`recording-frame-stats` verifies held-frame classification, still screens, minute
buckets, the 300-minute bound and lagged-tick attribution. Neither test binary
needs an OBS runtime.

`--self-test --output ABSOLUTE.mp4` runs a bounded five-second manual diagnostic
using generated color video and a generated 440 Hz audio tone. It loads no capture
modules, reads no game process and captures no screen/window/audio device. It
exercises the actual hardware encoder, AAC, the fragmented MP4 and its finalized
index on a clean stop, packet-clock signal and stop path. This mode is not
exposed through the Electron recording driver.
