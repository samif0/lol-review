# Contributing to Revu

Thanks for taking a look. Revu is a single-developer project right now, so the
contribution flow is informal: **DM me first** before opening a non-trivial
PR. Tracking issues and discussing scope up front saves both of us the
churn of an unwanted refactor or a feature that overlaps something I'm
already building. For tiny fixes (typos, broken links, obvious bug fixes)
just open the PR — no preamble needed.

Development requires Windows 10/11 x64, the .NET 8 SDK, and Node 22.12 or newer.
From `desktop/`, run `npm ci --ignore-scripts`, then `npm run setup` to install
the pinned Overwolf Electron runtime and build the sidecar. `npm start` opens
the normal application using your existing Revu data. Use
`npm run start:isolated` for a restricted scratch profile; see
[README.md](README.md) for commands and the host/frontend/backend layout.

Run both `src/Revu.Core.Tests/Revu.Core.Tests.csproj` and
`src/Revu.Sidecar.Tests/Revu.Sidecar.Tests.csproj` with
`dotnet test <project> -c Release -p:Platform=x64`. The platform flag is required.
These suites use temporary SQLite fixtures rather than your real database.
For desktop changes, run `npm test`, `npm run build`, `npm run check:probes`,
and `npm run test:probes` from `desktop/` after the dependency install.

`npm run readiness` checks runtime startup without gaming packages. Live GEP
and Recorder tests require reviewed access and explicit manual activation;
never put developer credentials in code, logs, or fixtures. A passing diagnostic
does not establish recording approval or production capture behavior.

PR-acceptance bar: builds clean (0 warnings, 0 errors on Release), tests
pass, and the change is scoped to a single concern — a bug fix doesn't
need surrounding cleanup, a one-shot operation doesn't need a helper.
Please don't add features without filing an issue first. For bug reports,
use the GitHub issue templates; for feature requests, the same. For
security issues, see [SECURITY.md](SECURITY.md).
