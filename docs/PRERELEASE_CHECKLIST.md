# Pre-Release Checklist

Run this before creating a release tag.

## Required

- `.\scripts\test-core.ps1`
- `.\scripts\build-app.ps1`
- `.\scripts\test-coach.ps1`
- `.\scripts\test-proxy.ps1`
- Site link smoke check for `site/index.html`, `site/privacy.html`, and `site/terms.html`
- Coach package check with `.\scripts\package-coach.ps1`

## Conditional

- `.\scripts\package-coach.ps1 -Ml` when the optional ML pack changed.
- `.\scripts\perf-long-soak.ps1 -Hours 24` before major UX/media releases.

## Release Automation

CI remains release-only by decision: use `.github/workflows/release.yml` through
tag/manual release. Do not add normal PR CI unless that product preference
changes.
