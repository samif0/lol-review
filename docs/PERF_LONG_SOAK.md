# Long-Soak Performance Harness

Use this before major releases when memory growth, media cleanup, or long idle
stability is in scope.

## Harness

Run the app, then start:

```powershell
.\scripts\perf-long-soak.ps1 -Hours 24 -SampleMinutes 5
```

The script writes a CSV under `docs/` with timestamp, process id, private MB,
working set MB, handle count, and CPU seconds.

## Manual Loop

During the first hour, cycle through:

1. Dashboard
2. Session logger
3. Review page
4. VOD player with a real recording
5. Settings

For VOD checks, open and close the same recording at least 20 times. Then leave
the app idle overnight.

## Pass Criteria

- Private working set reaches a plateau instead of growing monotonically.
- Handle count does not climb continuously after navigation stops.
- CPU is near idle after the navigation loop.
- Any confirmed leak gets a repro note with owner files and the CSV attached to
  the release issue.
