# Reddit Pre-Alpha Runbook

Goal: get 10-25 real installs from League players, learn where onboarding
breaks, and avoid scaling support before the app has cohort feedback.

Coach is part of the build, but it is alpha. Ship Revu anyway and collect
coach feedback separately from the core loop. Core feedback is install, first
launch, League detection, post-game review, objectives, rules, VOD linking,
and whether the app helps players review ranked games. Coach feedback is
usefulness, setup friction, wrong answers, latency, and whether the output is
grounded enough to trust.

## Launch Bar

Ship the Reddit post only after these are true:

- Latest `Setup.exe` exists on GitHub Releases and downloads from the site in one click.
- `docs/PRERELEASE_CHECKLIST.md` has a current local pass.
- `docs/KNOWN_ISSUES.md` has no current blocker, or the blocker is named in the post.
- You can respond to issues within 48 hours on weekdays.
- You have one clean rollback plan: unlist or edit the Reddit post, pause the release, and point users at the previous release.
- Coach is clearly labeled alpha in the app and does not block the rest of the release unless it causes install, launch, data-safety, or core navigation failures.

## Suggested Post

Title:

```text
I built a local-first League review app for ranked players and need pre-alpha testers
```

Body:

```text
I'm looking for a small pre-alpha group for Revu, a Windows desktop app that reviews ranked League games with you.

What it does:
- Detects ranked games and captures post-game stats automatically
- Prompts a quick structured review after each game
- Tracks objectives/rules across sessions
- Links VODs if you record with Ascent
- Includes an alpha AI coach you can enable from Settings
- Stores your data locally in SQLite, no telemetry

What I need feedback on:
- Does install/first launch work on your machine?
- Does the app correctly detect champ select and game end?
- Is the post-game review flow useful or annoying?
- If you try the coach: was setup understandable, and did the answers feel grounded?
- What confused you in the first 10 minutes?

This is pre-alpha, Windows only, unsigned, and League-focused. The AI coach is included but alpha; expect rough edges and please call them out separately from the core review flow. If you're uncomfortable running unsigned hobby software, wait for a later build. Source is public here: https://github.com/samif0/lol-review

Download: https://revu.lol
Bug reports: https://github.com/samif0/lol-review/issues
```

## Intake Triage

For every report, capture:

- App version and Windows version.
- Install source: site download or GitHub release.
- Whether League client was open.
- Whether the problem is install, launch, detection, review flow, VOD, coach, update, or data.
- For coach reports, capture provider, model, whether the runtime was installed, whether an API key/local model was configured, and the exact prompt/result if the user is willing to share it.
- Log folder contents if relevant: Settings -> About -> Open log folder.

## Stop Conditions

Pause outreach if any of these happen twice:

- First launch crash.
- Installer/update failure with no workaround.
- Database corruption or data loss.
- League detection blocks the UI or repeatedly misfires.
- Security/privacy concern you cannot answer clearly.

## First Follow-Up

After 3-5 days, post or DM a short follow-up:

```text
If you tried Revu: what was the first moment where you got stuck, confused, or thought "I won't use this"?
```

That answer is more useful than feature requests during pre-alpha.
