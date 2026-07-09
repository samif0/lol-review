# Revu — project context

Revu is a Windows desktop app for reviewing League of Legends games. Tauri UI, C# core, C# sidecar, SQLite storage under %LOCALAPPDATA%. **This repo is public.**

## Components

| Path | What it is | Verified by |
|---|---|---|
| `src/Revu.Core` | Domain + persistence (C#, .NET 8) | `src/Revu.Core.Tests` |
| `src/Revu.Sidecar` | Local HTTP sidecar the UI talks to | `src/Revu.Sidecar.Tests` |
| `desktop/` | Tauri app (Vite UI + `src-tauri` Rust shell) | `npm run build` (no behavioral tests yet) |
| `proxy/` | Cloudflare Worker for Match-V5 lookups | its own tests; **deployed manually via wrangler** |
| `site/` | Marketing site | n/a |

## Verify commands (a change is not done until these pass)

```
dotnet test src/Revu.Core.Tests/Revu.Core.Tests.csproj -c Release -p:Platform=x64
dotnet test src/Revu.Sidecar.Tests/Revu.Sidecar.Tests.csproj -c Release -p:Platform=x64
cd desktop && npm ci && npm run build        # required only if desktop/** changed
```

CI (`.github/workflows/ci.yml`) runs the same commands on every PR and push to main. Releases are tag-triggered (`v*`) via `.github/workflows/release.yml` → Velopack (packId `LoLReview` — never change it; installed apps update in place).

## Autonomous loop

An automated research→implement loop operates on this repo. Its state contract lives at `automation/research/contract.md` (local-only, gitignored). Skills: `revu-research-digest`, `revu-implement`, `revu-janitor`. Loop branches are named `loop/RVU-NNN`; loop commits are titled `RVU-NNN: <title>`.

## Hard rules (apply to every session, human-driven or scheduled)

1. **Never stage or commit anything under `automation/`, `.claude/`, `docs/`, `experiments/`, or `tools/`.** They are gitignored on purpose — `automation/` contains personal game data and this repo is public. Never use `git add -f` or edit `.gitignore` to work around this.
2. **Never read or copy `automation/research/snapshots/`** except inside the digest skill's analysis step, and never quote raw snapshot contents into commits, PRs, issues, or logs.
3. **`proxy/` is out of scope for the autonomous loop.** Changes there require manual deployment; the loop must not propose or make them.
4. **Do not modify `.github/workflows/`** unless the task explicitly says so. Loop items never say so.
5. Platform is x64, .NET 8. Match existing code style; C# work follows the conventions already in `src/`.
6. If the working tree is dirty at the start of a scheduled run, stop and log — never stash or discard someone's work in progress.
