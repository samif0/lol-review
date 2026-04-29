# Telemetry decision

**v1 ships with no telemetry.** No analytics SDK, no usage events, no
crash beacons sent off-device, no remote feature flags that report
behavior. The only outbound network calls are the ones the user
explicitly relies on: Riot Match-V5 (via the Cloudflare Worker proxy),
GitHub Releases (auto-updater), CommunityDragon (champion static data),
and the local LCU. This is documented publicly on
[`site/privacy.html`](../site/privacy.html) and is the
default-on-install behavior — there's no opt-in toggle to flip because
there's nothing to opt in to.

**Why.** First-cohort users are paying with their trust, not their
money. A League tool that reads the LCU and watches every game has the
worst possible privacy optics for adding telemetry on top. The cost of
deferring telemetry is bug reports being lower-fidelity (we don't see
the silent crash on someone else's box); the cost of *not* deferring
it is "wait, what's this app sending home?" — a question we can't
answer well right now without dedicated infra (consent UI, retention
policy, deletion endpoint).

**When we'd revisit.** Once any of these hold:

1. ≥50 active installs and we have ≥3 weeks of "users hit a bug, our
   only signal is GitHub issues, repro is impossible" pain.
2. We need to make a product call (e.g. "should we cut the Coach
   feature?") that depends on actual usage frequency, not a vibe.
3. A specific reliability problem (update path, LCU connection,
   anything) that we can only diagnose with structured client signals.

If any of those land, the path is: opt-in toggle in Settings → Privacy
defaulting OFF, single endpoint to our own Cloudflare Worker (no
third-party SDK), aggressive PII redaction, public retention/deletion
policy, callout in the version's release notes. Not before.
