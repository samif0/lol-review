# Contributing to Revu

Thanks for taking a look. Revu is a single-developer project right now, so the
contribution flow is informal: **DM me first** before opening a non-trivial
PR. Tracking issues and discussing scope up front saves both of us the
churn of an unwanted refactor or a feature that overlaps something I'm
already building. For tiny fixes (typos, broken links, obvious bug fixes)
just open the PR — no preamble needed.

To build locally you need Windows 10/11, the .NET 8 SDK, Node 20+, and Rust
stable with the Tauri v2 prerequisites. Clone, then
`dotnet build Revu.sln -c Debug -p:Platform=x64` for the backend and
`cd desktop && npm ci && npm run tauri dev` for the app (see the README's
Development section for the full layout). The tests live in
`src/Revu.Core.Tests/` and `src/Revu.Sidecar.Tests/` and run with
`dotnet test <project> -c Release -p:Platform=x64` (the `-p:Platform=x64` is
required — the projects only declare an x64 platform). The data layer uses an
isolated SQLite test fixture so tests don't touch your real Revu database at
`%LOCALAPPDATA%\LoLReviewData\revu.db`.

PR-acceptance bar: builds clean (0 warnings, 0 errors on Release), tests
pass, and the change is scoped to a single concern — a bug fix doesn't
need surrounding cleanup, a one-shot operation doesn't need a helper.
Please don't add features without filing an issue first. For bug reports,
use the GitHub issue templates; for feature requests, the same. For
security issues, see [SECURITY.md](SECURITY.md).
