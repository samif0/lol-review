# Revu — project context

Revu is a Windows desktop app for reviewing League of Legends games. Overwolf Electron is its only desktop host, with a sandboxed JavaScript UI, C# core, C# sidecar, and SQLite storage under %LOCALAPPDATA%. **This repo is public.**

## Components

| Path | What it is | Verified by |
|---|---|---|
| `src/Revu.Core` | Domain + persistence (C#, .NET 8) | `src/Revu.Core.Tests` |
| `src/Revu.Sidecar` | Backend: `Hosting/` startup/composition; `Endpoints/` feature routes | `src/Revu.Sidecar.Tests` |
| `desktop/electron` | Overwolf Electron host, preload, native operations, child lifetime | `cd desktop; npm test` |
| `desktop/ui` | JavaScript pages and desktop platform adapter | `cd desktop; npm test; npm run build` |
| `desktop/runtime` | Shared pinned Overwolf runtime setup | `cd desktop; npm test` |
| `desktop/overwolf-probes` | Separate, manually gated gaming diagnostics | `npm run check:probes` + `npm run test:probes` from `desktop` |
| `proxy/` | Cloudflare Worker for Match-V5 lookups | its own tests; **deployed manually via wrangler** |
| `site/` | Marketing site | n/a |

## Verify commands (a change is not done until these pass)

```
dotnet test src/Revu.Core.Tests/Revu.Core.Tests.csproj -c Release -p:Platform=x64
dotnet test src/Revu.Sidecar.Tests/Revu.Sidecar.Tests.csproj -c Release -p:Platform=x64
cd desktop
npm ci --ignore-scripts
npm test
npm run build
npm run check:probes
npm run test:probes
```

The desktop commands are required when `desktop/**` changes. Use Node 22.12 or newer. `npm run setup` explicitly installs/verifies the runtime and builds the sidecar under `artifacts/desktop-build`. `npm start` uses existing production Revu data; `npm run start:isolated` uses a restricted scratch profile. `npm run readiness` exercises runtime startup without gaming packages. None of these diagnostics establishes live GEP/Recorder approval.

CI lives in `.github/workflows/ci.yml`. Releases are tag-triggered (`v*`) through `.github/workflows/release.yml` and Velopack. Preserve packId `LoLReview` and main executable `revu-desktop.exe`. `npm run package:dir` produces `artifacts/desktop-package/win-unpacked` locally without publishing.

The sidecar must retain exclusive data ownership, schema downgrade checks, separate read-only/write-capable database graphs, and graceful draining of accepted work. Host promotion does not authorize schema or data migration changes. Never describe the runtime as telemetry-free: disabling optional Overwolf analytics still leaves its mandatory minimal runtime telemetry.

## Autonomous loop

An automated research→implement loop operates on this repo. Its state contract lives at `automation/research/contract.md` (local-only, gitignored). Skills: `revu-research-digest`, `revu-implement`, `revu-janitor`. Loop branches are named `loop/RVU-NNN`; loop commits are titled `RVU-NNN: <title>`.

## Hard rules (apply to every session, human-driven or scheduled)

1. **Never stage or commit anything under `automation/`, `.claude/`, `docs/`, `experiments/`, or `tools/`.** They are gitignored on purpose — `automation/` contains personal game data and this repo is public. Never use `git add -f` or edit `.gitignore` to work around this.
2. **Never read or copy `automation/research/snapshots/`** except inside the digest skill's analysis step, and never quote raw snapshot contents into commits, PRs, issues, or logs.
3. **`proxy/` is out of scope for the autonomous loop.** Changes there require manual deployment; the loop must not propose or make them.
4. **Do not modify `.github/workflows/`** unless the task explicitly says so. Loop items never say so.
5. Platform is x64, .NET 8. Match existing code style; C# work follows the conventions already in `src/`.
6. If the working tree is dirty at the start of a scheduled run, stop and log — never stash or discard someone's work in progress.
