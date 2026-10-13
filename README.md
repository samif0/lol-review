# Revu

A Windows desktop app that helps you review your League of Legends games.

[![Download latest release](https://img.shields.io/github/v/release/samif0/lol-review?label=Download&style=for-the-badge)](https://github.com/samif0/lol-review/releases/latest)

Revu sits alongside the League client and turns each game into a short, structured review. It detects champ select and prompts you for a pre-game intention, captures your post-game stats, and walks you through a reflection: what went well, what went wrong, and what to focus on next. Objectives you set are journaled across the games where you practiced them, and linked local VODs keep your notes timestamped to the moment. Built-in recording uses a bundled OBS engine to capture the League window and game audio, with automatic NVIDIA, AMD or Intel hardware encoder selection. Recording is a beta feature and stays off until you enable it in Settings. Existing Ascent folder imports remain available.

Rules you set (a daily game cap, a loss-streak cool-off, a curfew) can be flagged as **hard stops**: while one is tripped, Revu cancels the League client's own queue instead of just showing you a warning, and shows you the plan you wrote for that moment. An override is one click, but only after a 60-second hold, and it's counted.

Reviews and objectives are stored locally in SQLite, with no cloud synchronization. Account linking and Match-V5 lookups use Revu's Cloudflare proxy; sharing a clip uploads the selected media. The Overwolf Electron host disables optional anonymous analytics, but Overwolf retains mandatory minimal runtime telemetry, including launch and basic session signals. See [Overwolf's analytics description](https://dev.overwolf.com/ow-electron/getting-started/onboarding-resources/ow-electron-technical-overview/#app-usage-analytics).

## Install

Download the latest **`Revu-Setup.exe`** from [Releases](https://github.com/samif0/lol-review/releases/latest), run it, and launch Revu from the Start Menu. Auto-update is built in, so you won't need to download it again. There's a walkthrough at [revu.lol](https://revu.lol).

## Development

### Requirements

- Windows 10/11 (x64)
- .NET 8 SDK
- Node 22.12 or newer
- Visual Studio C++ x64 Build Tools and the Windows SDK (for building the bundled recorder)
- League of Legends client (for the live monitoring features)
- Optional: `ffmpeg.exe` placed at `deps\ffmpeg.exe` to enable clip extraction
  (the release workflow downloads this automatically; for local dev, drop your
  own copy there)

### Build and run

Overwolf Electron is the only desktop host. Its main process owns windows,
native dialogs, media access, and the C# sidecar's lifetime. The sandboxed UI
uses a narrow preload API; the sidecar owns persistence and application workflows.

```powershell
cd desktop
npm ci --ignore-scripts
npm run setup
npm start
```

`setup` installs and verifies the pinned Overwolf Electron runtime and builds
the sidecar into `artifacts/desktop-build/`. **`npm start` opens your existing
Revu database and enables normal application behavior.**

Run `npm run recorder:setup` to build the pinned OBS helper and runtime under
`artifacts/desktop-recorder/`. `npm run package:dir` includes it automatically.
The installed GPU driver must support hardware H.264 encoding on the recording
graphics adapter. See [recording setup and diagnostics](desktop/electron/RECORDING.md)
for presets, recovery and the separate Practice Tool test launch.

Use `npm run start:isolated` for a separate diagnostic profile and database under
`artifacts/desktop-preview/`. It restricts operations with external effects and
does not write to production Revu data. `npm run readiness` checks actual runtime
startup and renderer loading without requesting gaming packages.

Neither startup check enables automatic recording or proves Overwolf package
approval. GEP and Recorder remain separate, manually enabled diagnostics that
require reviewed access and a developer key. See the
[probe setup instructions](desktop/overwolf-probes/README.md).

`npm run build` compiles the UI; `npm run dev` provides its Vite development
preview. Neither command starts the desktop host.

### Test

A change is not done until both backend suites pass:

```powershell
dotnet test src\Revu.Core.Tests\Revu.Core.Tests.csproj -c Release -p:Platform=x64
dotnet test src\Revu.Sidecar.Tests\Revu.Sidecar.Tests.csproj -c Release -p:Platform=x64
```

`-p:Platform=x64` is required. For desktop changes, also run from `desktop/`:

```powershell
npm ci --ignore-scripts
npm test
npm run build
npm run check:probes
npm run test:probes
```

Backend tests use temporary SQLite fixtures. Runtime readiness is a separate
local diagnostic; live gaming activation does not belong in CI.

### Project layout

```text
src/
  Revu.Core/          Domain + persistence — SQLite, repositories, services,
                      LCU integration, migrations (.NET 8)
  Revu.Core.Tests/    xUnit tests over temp-file SQLite fixtures
  Revu.Sidecar/       Application backend and authenticated loopback API
    Hosting/         Composition, startup, authentication, shutdown
    Endpoints/       API routes grouped by feature and request contracts
  Revu.Sidecar.Tests/ API, persistence, and lifecycle contract tests
desktop/
  electron/          Overwolf Electron main process and sandboxed preload
  ui/                Vanilla JavaScript pages and desktop platform adapter
  runtime/           Shared pinned runtime installation and verification
  recorder/          Pinned OBS helper, hardware encoder policy and source bundle
  overwolf-probes/   Separate GEP, Recorder, and readiness diagnostics
site/                 Static landing site at revu.lol
proxy/                Cloudflare Worker that proxies Riot Match-V5
.github/              Issue templates, CI, and the release workflow
```

## Contributing

Contributions are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md) for the
workflow, then:

- **Bugs and features** — open an issue via the [templates](https://github.com/samif0/lol-review/issues/new/choose).
- **Security issues** — see [SECURITY.md](SECURITY.md); please don't file these as public issues.
- **Code** — fork, branch, and open a PR.

## Releases

Build a Windows application folder locally from `desktop/`:

```powershell
npm run package:dir
```

The result is `artifacts/desktop-package/win-unpacked/`, containing
`revu-desktop.exe`, the self-contained Windows sidecar, and application resources.
This local build does not publish a release.

The tag-triggered (`v*`) `.github/workflows/release.yml` builds the Overwolf
Electron application folder and packages it through Velopack. Preserve the
installed identity **`LoLReview`** and main executable **`revu-desktop.exe`** so
existing installations remain on the same update chain. Installer signing,
fresh installation, and upgrading an existing installation require release
validation; a successful folder build alone does not prove those paths.

## Data and logs

For reference, Revu keeps user data and logs under `%LOCALAPPDATA%`:

- Database: `LoLReviewData\revu.db`
- Config: `LoLReviewData\config.json`
- Backups: `LoLReviewData\backups\`
- Clips: `LoLReviewData\clips\`
- App logs: `Revu\logs\` (Settings → About → "Open log folder")
- Electron profile: `Revu\ElectronProfile\`
- Runtime logs: `Revu\logs\runtime\`
- Updater log: `LoLReview\velopack.log` (Settings → About → "Diagnose update")

## License

Revu's application source is MIT — see [LICENSE](LICENSE).

The separate recorder helper, OBS engine and codec dependencies carry their
own GPL and other licenses. Every recorder package includes their notices,
original license texts and matching source. See the
[recorder notices](desktop/recorder/THIRD-PARTY-NOTICES.txt).

> *Revu isn't endorsed by Riot Games and doesn't reflect the views or opinions
> of anyone officially involved in producing or managing League of Legends.
> League of Legends and Riot Games are trademarks or registered trademarks of
> Riot Games, Inc.*
