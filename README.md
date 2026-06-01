# Revu

A Windows desktop app that helps you review your League of Legends games.

[![Download latest release](https://img.shields.io/github/v/release/samif0/lol-review?label=Download&style=for-the-badge)](https://github.com/samif0/lol-review/releases/latest)

Revu sits alongside the League client and turns each game into a short, structured review. It detects champ select and prompts you for a pre-game intention, captures your post-game stats, and walks you through a reflection: what went well, what went wrong, and what to focus on next. Objectives you set are journaled across the games where you practiced them, and optional VOD recording (via [Ascent](https://tryascent.gg)) auto-links so your notes are timestamped to the moment.

Your data stays local — a SQLite database under `%LOCALAPPDATA%`, no cloud sync and no telemetry. Your Riot ID and region are sent only to a Cloudflare Worker proxy for Match-V5 lookups.

## Install

Download the latest **`Revu-Setup.exe`** from [Releases](https://github.com/samif0/lol-review/releases/latest), run it, and launch Revu from the Start Menu. Auto-update is built in, so you won't need to download it again. There's a walkthrough at [revu.lol](https://revu.lol).

## Development

### Requirements

- Windows 10/11 (x64 or arm64)
- .NET 8 SDK
- Visual Studio 2022 or MSBuild Build Tools
- League of Legends client (for the live monitoring features)
- Optional: `ffmpeg.exe` placed at `deps\ffmpeg.exe` to enable clip extraction
  (the release workflow downloads this automatically; for local dev, drop your
  own copy there)

### Build and run

```powershell
dotnet build src\Revu.App\Revu.App.csproj -c Debug -p:Platform=x64
```

The build outputs to `src\Revu.App\bin\x64\Debug\`. The executable is named
`LoLReview.App.exe` (the historical package id; the product is Revu). Launch it
with `run.bat`, or directly:

```powershell
src\Revu.App\bin\x64\Debug\LoLReview.App.exe
```

### Test

```powershell
dotnet test src\Revu.Core.Tests\
```

### Project layout

```text
src/
  Revu.App/        WinUI 3 desktop app — DI, views, view models, update flow
  Revu.Core/       SQLite, repositories, services, LCU integration, migrations
  Revu.Core.Tests/ xUnit tests over temp-file SQLite fixtures
site/              Static landing site at revu.lol
proxy/             Cloudflare Worker that proxies Riot Match-V5
docs/              Architecture and operational docs
.github/           Issue templates and the release workflow
```

For a deeper tour, see [docs/CODEBASE_ONBOARDING.md](docs/CODEBASE_ONBOARDING.md).

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
git tag -a v2.17.0 -m "v2.17.0"
git push origin main v2.17.0
```

The workflow runs the tests, stamps the version into the csproj, publishes a
self-contained `win-x64` build with MSBuild, bundles `ffmpeg.exe` if present,
signs the binaries, packs with `vpk`, and creates the GitHub Release that the
in-app updater consumes. It also publishes a cleanly-named `Revu-Setup.exe`
asset. The workflow triggers on any tag beginning with `v` (e.g. `v2.17.19`).

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
