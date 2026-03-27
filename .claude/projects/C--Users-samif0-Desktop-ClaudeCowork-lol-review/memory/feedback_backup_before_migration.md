---
name: Always backup DB before destructive operations
description: ALWAYS back up the user's database before any operation that could overwrite it — installs, migrations, schema changes, file copies
type: feedback
---

Always back up `lol_review.db` before ANY operation that could replace or overwrite it.

**Why:** During the Python→C# Velopack migration, the Setup.exe installed into the same directory as the user's data (`%LOCALAPPDATA%\LoLReview\`). The fresh app startup created an empty DB that overwrote the user's real data (games from March 9–26 were permanently lost). No backup existed because backups weren't enabled in config.

**How to apply:** Before any install, migration, DB copy, or schema change — always create a timestamped `.bak` copy first. Add backup logic to Velopack first-run hooks. Never assume data is safe just because the path looks right.
