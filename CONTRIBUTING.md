# Contributing to Revu

Thanks for taking a look. Revu is a single-developer project right now, so the
contribution flow is informal: **DM me first** before opening a non-trivial
PR. Tracking issues and discussing scope up front saves both of us the
churn of an unwanted refactor or a feature that overlaps something I'm
already building. For tiny fixes (typos, broken links, obvious bug fixes)
just open the PR — no preamble needed.

Development requires Windows 10/11 x64, the .NET 8 SDK pinned in `global.json`
(8.0.419 or a later 8.0.4xx patch), and Node 22.12 or newer. Clone into a short
path such as `C:\src\revu`: in a deeply nested checkout the sidecar test output
passes the Windows MAX_PATH limit, the native SQLite library (`e_sqlite3.dll`)
fails to load, and the sidecar tests fail for reasons unrelated to your change.
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
For `proxy/`, run `npm ci`, `npm run typecheck` and `npm test` there; for
`site/`, run `node --test site/tests` from the repository root.

Each .NET project commits a `packages.lock.json`, and CI restores in locked
mode. After changing a `PackageReference`, run a normal `dotnet restore` and
commit the updated lock files with the change.

`npm run readiness` checks runtime startup without gaming packages. Live GEP
and Recorder tests require reviewed access and explicit manual activation;
never put developer credentials in code, logs, or fixtures. A passing diagnostic
does not establish recording approval or production capture behavior.

PR-acceptance bar: builds clean (0 warnings, 0 errors on Release; Release
builds treat warnings as errors), CI is green, and the change is scoped to
a single concern: a bug fix doesn't need surrounding cleanup, a one-shot
operation doesn't need a helper.
Please don't add features without filing an issue first. For bug reports,
use the GitHub issue templates; for feature requests, the same. For
security issues, see [SECURITY.md](SECURITY.md).

## Releases and deploys (maintainer)

Only reviewed `main` ships. `release.yml` refuses a `v*` tag whose commit is
not on `origin/main`, and a hyphenated tag such as `v3.15.0-beta.1` publishes
as a GitHub prerelease that installed apps never auto-update to. Because that
check runs from the tagged commit's own workflow file, a repository ruleset
that limits who can create `v*` tags is the durable control.

`tag-release.yml` (the tagging path for sessions whose Git credentials get a
403 on `refs/tags/*`) pushes the tag with `GITHUB_TOKEN`, as
`github-actions[bot]`, so that ruleset blocks it too. Pick one when you add
the ruleset:

- Add GitHub Actions to the ruleset's bypass list. Any workflow that holds
  `contents: write` can then create a `v*` tag, so only do this while you are
  the only person with write access.
- Leave Actions out. `tag-release.yml` then fails, and release tags are pushed
  only from a machine whose own credentials can create them.

The proxy and site are deployed by hand: `npm run deploy` in `proxy/` and
`node scripts/deploy-site.mjs` from the repository root both run
`scripts/guard-deploy.mjs` first, which refuses unless the tree is clean,
`HEAD` equals `origin/main` after a fetch, and that package's tests pass. The
site deploy uploads only the public files of `site/`.
