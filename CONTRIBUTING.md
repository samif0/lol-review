# Contributing to Revu

Thanks for taking a look. Revu is a single-developer project right now, so the
contribution flow is informal: **DM me first** before opening a non-trivial
PR. Tracking issues and discussing scope up front saves both of us the
churn of an unwanted refactor or a feature that overlaps something I'm
already building. For tiny fixes (typos, broken links, obvious bug fixes)
just open the PR — no preamble needed.

To build locally you need .NET 8 SDK, Windows 10/11, and Visual Studio
2022 or VS Code with the C# extension. Clone, then
`dotnet build src/Revu.App/Revu.App.csproj -c Debug -r win-x64`. The
unit tests live in `src/Revu.Core.Tests/` and run with `dotnet test`. The
data layer uses an isolated SQLite test fixture so tests don't touch your
real Revu database at `%LOCALAPPDATA%\LoLReviewData\revu.db`. Architecture
notes are in `docs/CODEBASE_ONBOARDING.md`.

PR-acceptance bar: builds clean (0 warnings, 0 errors on Release), tests
pass, and the change is scoped to a single concern — a bug fix doesn't
need surrounding cleanup, a one-shot operation doesn't need a helper.
Please don't add features without filing an issue first. For bug reports,
use the GitHub issue templates; for feature requests, the same. For
security issues, see [SECURITY.md](SECURITY.md).
