# Pre-Release Checklist

Run this before creating a release tag.

## Required

- `.\scripts\test-core.ps1`
- `.\scripts\build-app.ps1`
- `.\scripts\test-coach.ps1`
- `.\scripts\test-proxy.ps1`
- Site link smoke check for `site/index.html`, `site/privacy.html`, and `site/terms.html`
- Coach package check with `.\scripts\package-coach.ps1`

AI coach is included in the build but labeled alpha. Do not block the core app
release on coach answer quality, but do block on coach-caused install, launch,
data-safety, navigation, or Settings failures.

## Last Local Pass

Validated on 2026-05-16:

- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-core.ps1` - passed, 116 tests.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-coach.ps1` - passed, 4 tests.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-proxy.ps1` - passed, 4 tests.
- `dotnet build src\Revu.App\Revu.App.csproj -c Release -r win-x64` - passed, 0 warnings.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-app.ps1` - passed.
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-coach.ps1` - passed.
- Site local asset smoke check - passed.

Note: the repo PowerShell scripts were blocked by the machine execution
policy when run directly. Use `-ExecutionPolicy Bypass` for the current
process if this machine keeps the default restrictive policy.

## Conditional

- `.\scripts\package-coach.ps1 -Ml` when the optional ML pack changed.
- `.\scripts\perf-long-soak.ps1 -Hours 24` before major UX/media releases.

## Release Automation

CI remains release-only by decision: use `.github/workflows/release.yml` through
tag/manual release. Do not add normal PR CI unless that product preference
changes.
