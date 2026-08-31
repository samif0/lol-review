# Revu

A Windows desktop app that helps you review your League of Legends games.

[![Download latest release](https://img.shields.io/github/v/release/samif0/lol-review?label=Download&style=for-the-badge)](https://github.com/samif0/lol-review/releases/latest)

Revu sits alongside the League client and turns each game into a short, structured review. It detects champ select and prompts you for a pre-game intention, captures your post-game stats, and walks you through a reflection: what went well, what went wrong, and what to focus on next. Objectives you set are journaled across the games where you practiced them, and optional VOD recording (via [Ascent](https://tryascent.gg)) auto-links so your notes are timestamped to the moment.

Your data stays local — a SQLite database under `%LOCALAPPDATA%`, no cloud sync and no telemetry. Your Riot ID and region are sent only to a Cloudflare Worker proxy for Match-V5 lookups.

## Install

Download the latest **`Revu-Setup.exe`** from [Releases](https://github.com/samif0/lol-review/releases/latest), run it, and launch Revu from the Start Menu. Auto-update is built in, so you won't need to download it again. There's a walkthrough at [revu.lol](https://revu.lol).

## Development

### Requirements

- Windows 10/11 (x64)
- .NET 8 SDK
- Node 20+ (for the desktop UI)
- Rust stable + the [Tauri v2 prerequisites](https://v2.tauri.app/start/prerequisites/)
  (for the desktop shell)
- League of Legends client (for the live monitoring features)
- Optional: `ffmpeg.exe` placed at `deps\ffmpeg.exe` to enable clip extraction
  (the release workflow downloads this automatically; for local dev, drop your
  own copy there)

### Build and run

The app is a Tauri shell (`desktop/`) that spawns a C# sidecar
(`src/Revu.Sidecar`) — a local HTTP backend over the shared core library
(`src/Revu.Core`).

```powershell
# Backend (sidecar + core)
dotnet build Revu.sln -c Debug -p:Platform=x64

# Desktop app (spawns the sidecar it finds via the dev lookup)
cd desktop
npm ci
npm run tauri dev
```

### Test

A change is not done until both suites pass (CI runs the same commands):

```powershell
dotnet test src\Revu.Core.Tests\Revu.Core.Tests.csproj -c Release -p:Platform=x64
dotnet test src\Revu.Sidecar.Tests\Revu.Sidecar.Tests.csproj -c Release -p:Platform=x64
```

`-p:Platform=x64` is required — the projects declare only an x64 platform, so a
default AnyCPU run fails. The UI compile gate is `cd desktop && npm ci && npm run build`.

### Project layout

```text
src/
  Revu.Core/          Domain + persistence — SQLite, repositories, services,
                      LCU integration, migrations (.NET 8)
  Revu.Core.Tests/    xUnit tests over temp-file SQLite fixtures
  Revu.Sidecar/       Local HTTP sidecar the desktop UI talks to
  Revu.Sidecar.Tests/ Write-contract tests for the sidecar
desktop/              Tauri app — ui/ (vanilla JS pages) + src-tauri (Rust shell)
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

Releases are automated by `.github/workflows/release.yml`. To publish a version,
commit to `main`, then tag and push:

```powershell
git tag -a v3.4.0 -m "v3.4.0"
git push origin main v3.4.0
```

The workflow runs the tests, stamps the version into `tauri.conf.json`, builds
the Tauri app (cargo) and a self-contained `win-x64` sidecar, bundles
`ffmpeg.exe` if present, signs the binaries, packs with `vpk` (packId stays
`LoLReview` so existing installs update in place), and creates the GitHub
Release that the in-app updater consumes. It also publishes a cleanly-named
`Revu-Setup.exe` asset. The workflow triggers on any tag beginning with `v`
(e.g. `v3.3.3`).

## Data and logs

For reference, Revu keeps user data and logs under `%LOCALAPPDATA%`:

- Database: `LoLReviewData\revu.db`
- Config: `LoLReviewData\config.json`
- Backups: `LoLReviewData\backups\`
- Clips: `LoLReviewData\clips\`
- App logs: `Revu\` (Settings → About → "Open log folder")
- Updater log: `LoLReview\velopack.log` (Settings → About → "Diagnose update")

## License

MIT — see [LICENSE](LICENSE).

> *Revu isn't endorsed by Riot Games and doesn't reflect the views or opinions
> of anyone officially involved in producing or managing League of Legends.
> League of Legends and Riot Games are trademarks or registered trademarks of
> Riot Games, Inc.*
