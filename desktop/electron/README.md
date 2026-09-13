# Electron desktop host

Overwolf Electron is Revu's only desktop host. Build and launch from `desktop/`;
see the [root README](../../README.md#development) for setup and commands.
There is one dependency lockfile at `desktop/package-lock.json`.

- `main.mjs`: window creation, IPC admission, events, and application lifetime.
- `configuration.mjs`: normal/isolated launch options, profile and packaged paths.
- `native.mjs`: dialogs, exports, window sizing, external URLs and installed updates.
- `sidecar.mjs`: launch-specific authenticated backend ownership, HTTP and SSE.
- `protocols.mjs`: application assets and the renderer's network policy.
- `media.mjs`: revocable media grants and byte-range playback.
- `preload.cjs`: the sandboxed renderer API; filesystem and bearer access stay private.

The shared command catalog is `../ui/platform/commands.mjs`. Same-origin child
pages deliberately use the trusted top document's bridge. The renderer uses its
own Chromium session, so its network restrictions don't affect runtime-owned
windows. Automatic match recording, match attachment, Settings and background
behavior are implemented; the ordinary unsigned build still requests no gaming
packages. The credentialed development launcher enables Recorder in a staged
copy of Revu. See [automatic recording](RECORDING.md) for setup, user behavior
and the live validation that still requires developer access. Separate manual
diagnostics remain in `../overwolf-probes/`.

Ascent imports are available as a temporary recording option without developer
access. In **Settings → Recording → Ascent recordings**, choose Ascent's output
folder, save it, and scan for existing videos. Keep Ascent recording your matches;
Revu links finished recordings on startup and after games. Imports reference the
original files, so keep them in that folder. Disconnecting the folder stops future
imports and preserves already linked videos. Built-in recording is marked
**Coming soon**, with its controls disabled until release; Ascent stays available.

For a separate local build, run `npm run package:dir` and open
`../../artifacts/desktop-package/win-unpacked/revu-desktop.exe`. Quit the installed
Revu first, including its tray process: both normal builds use your existing data.
The installed app is left in place; isolated previews use separate data and do not
monitor live matches.

## Data and lifecycle

`npm start` uses normal Revu data under `%LOCALAPPDATA%` (or an explicit
`--data-root`/`REVU_DATA_ROOT` override). `npm run start:isolated` uses a separate
scratch profile outside LocalAppData and disables live monitoring and external
writes. It also blocks updater operations, database replacement, and output-path
changes. The backend independently validates isolated storage and copied config.

The main process owns exactly one sidecar. Shutdown fences new commands, lets
submitted HTTP requests finish under their existing deadlines, and asks the
backend to drain background writes and release its database lease. Only its own
unresponsive child can be terminated; failed writes are never retried. Successful
reset/restore operations relaunch after shutdown. Velopack maintenance hooks exit
before data access. Installed updates retain `LoLReview` and `revu-desktop.exe`.

Packaged JavaScript and UI files live in `resources/app.asar`; the self-contained
`Revu.Sidecar.exe` and its dependencies sit beside `revu-desktop.exe`. Branding is
owned here in `branding/revu.svg`, with generated `branding/revu.png` and
`branding/revu.ico`. Run `node scripts/build-branding.mjs` from `desktop/` to
regenerate them with Inkscape on PATH (or set `INKSCAPE_PATH` to its executable).
The off-white review loop and right-facing play symbol use a charcoal tile;
the icon includes 16–256px frames for Windows display scaling. Packaging
also puts the icon in `resources/branding` for the Windows taskbar. The normal
Windows application ID is `gg.revu.desktop`; isolated previews use a separate ID.

## Verification

`npm test` from `desktop/` covers the host, platform, shared runtime and packaging.
From the repository root, `scripts/Test-ElectronHost.ps1` creates a fresh scratch
root and synthetic H.264/AAC fixture. Its real Chromium smoke checks rendered
snapshots, top/iframe bridge isolation, rejected commands, duplicate sidecar
ownership, playback/seek/refresh and graceful shutdown.

No developer key is required for these tests. Synthetic playback does not prove
League recording, audio isolation, capture recovery or performance. Signed
installation and upgrades still require release acceptance testing.

The root dependency overrides pin fixed esbuild and HTTP redirect helpers,
including the scoped builder helper's compatible upstream replacement. The
remaining npm audit entries come from the builder's Linux AppImage advisory;
this project packages Windows only. Recheck these overrides when updating the
official Overwolf builder. The pinned rcedit helper supplies Windows branding
without requiring administrator privileges to unpack the builder's legacy tools.
