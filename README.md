# Revu

Revu is a Windows desktop app for reviewing your League of Legends games locally. It watches for ranked game flow, surfaces pre-game and post-game prompts, and keeps your review history, notes, objectives, and session data in SQLite on your machine.

## What it does

- Shows a pre-game focus window during champ select
- Captures post-game stats and review notes after each match
- Tracks dashboard, history, losses, analytics, rules, objectives, and VOD review
- Filters out non-target game modes such as ARAM
- Stores all user data locally in SQLite
- Installs and updates through Velopack / GitHub Releases

## Install

Use the latest `Setup.exe` from [Releases](https://github.com/samif0/lol-review/releases).

1. Download the newest installer asset.
2. Run `Revu-Setup.exe` or the release `Setup.exe`.
3. Launch the installed app from Start Menu or Desktop.

Notes:

- Auto-update is handled by the installed app through GitHub Releases.
- The install root is `%LOCALAPPDATA%\LoLReview` (the Velopack `packId`, never renamed so auto-update keeps working).
- User data is stored separately in `%LOCALAPPDATA%\RevuData` so reinstalling the app does not wipe the database.
- On startup, the app migrates legacy DB / config / backup files forward from older locations when needed.

## Data and logs

Current user-data location:

```text
%LOCALAPPDATA%\RevuData\
```

Important files:

- Database: `%LOCALAPPDATA%\RevuData\revu.db`
- Config: `%LOCALAPPDATA%\RevuData\config.json`
- Safety backups: `%LOCALAPPDATA%\RevuData\backups\`
- Default clips folder: `%LOCALAPPDATA%\RevuData\clips\`

Install-owned files:

- App binaries: `%LOCALAPPDATA%\LoLReview\current\`
- Packages / updater files: `%LOCALAPPDATA%\LoLReview\packages\`

Logs:

- Startup log: `%LOCALAPPDATA%\LoLReview\startup.log`
- Crash log: `%LOCALAPPDATA%\LoLReview\crash.log`
- Velopack log: `%LOCALAPPDATA%\LoLReview\velopack.log`

## Development

### Requirements

- Windows 10/11
- .NET 8 SDK
- Visual Studio 2022 or MSBuild Build Tools
- League of Legends client for live monitoring features

Optional runtime dependency:

- `ffmpeg.exe` in `deps\` for clip extraction

### Open the solution

```powershell
start Revu.sln
```

### Build from CLI

```powershell
dotnet restore src\Revu.App\Revu.App.csproj -r win-x64
msbuild Revu.sln /p:Configuration=Debug /p:Platform=x64 /p:RuntimeIdentifier=win-x64
```

### Run a local debug build

After building `Debug|x64`, launch the app with:

```powershell
run.bat
```

Or run the built executable directly:

```text
src\Revu.App\bin\x64\Debug\net8.0-windows10.0.19041.0\Revu.App.exe
```

## Building a release locally

The GitHub Actions release workflow is the source of truth, but the local publish shape is:

```powershell
dotnet restore src\Revu.App\Revu.App.csproj -r win-x64
msbuild src\Revu.App\Revu.App.csproj `
  /p:Configuration=Release `
  /p:Platform=x64 `
  /p:RuntimeIdentifier=win-x64 `
  /p:SelfContained=true `
  /t:Publish
```

If `deps\ffmpeg.exe` exists, copy it into the publish output before packing.

## Releasing

Releases are automated through `.github/workflows/release.yml`.

To publish a new version:

1. Commit your changes to `main`.
2. Create a version tag such as `v2.3.1`.
3. Push `main` and the tag.

Example:

```powershell
git add .
git commit -m "fix: describe your release"
git tag -a v2.3.1 -m "v2.3.1"
git push origin main
git push origin v2.3.1
```

The workflow will:

- stamp `<Version>` in `src\Revu.App\Revu.App.csproj` from the tag
- restore and publish the WinUI app for `win-x64`
- include `ffmpeg.exe` if available
- pack the release with `vpk`
- publish the GitHub Release used by the in-app updater

Tag rules currently accepted by the workflow:

- `v2.[1-9].*`
- `v3.*`

## Project structure

```text
src/
  Revu.App/        WinUI 3 desktop app, DI wiring, views, view models, update flow
  Revu.Core/       SQLite, repositories, domain services, LCU integration, migrations
.github/workflows/
  release.yml           GitHub Actions release pipeline
run.bat                 Helper to launch the local debug build and print startup logs
Revu.sln           Visual Studio solution
```
