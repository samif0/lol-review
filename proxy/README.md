# revu-proxy

Cloudflare Worker behind the Revu desktop app and revu.lol: the Riot API
passthrough, email-code sign-in, clip sharing (R2 + D1), narration transcripts
(Workers AI) and the revu.lol web account API.

Who calls it, and how they authenticate:

- **Desktop app**: `Authorization: Bearer <session token>` from `/auth/verify`,
  stored hashed in D1 `sessions`. Every Bearer resolves to a real account. There
  is no static token allowlist: the old `ALLOWED_TOKENS` secret was deleted on
  2026-06-19 and the code path that read it is gone. Do not re-add it.
- **Web review tool** (revu.lol home page): a 30-minute JWT from `/web/session`
  after a Turnstile check, for the `/web/*` Riot passthrough only.
- **Web accounts** (revu.lol sign-in, My clips, Account): the `__Host-revu_web`
  cookie, read only by `/auth/web/*` (see [Web accounts](#web-accounts-revulol)).

The Worker forwards Riot calls with the server-side `RIOT_API_KEY`; users never
see it. Only the Riot passthrough needs it: sign-in, sign-out, clips and
transcripts keep working while it is missing or being rotated.

## Setup

```sh
cd proxy
npm install
wrangler login   # one-time, opens browser
```

## Secrets

```sh
# Your permanent Riot key
wrangler secret put RIOT_API_KEY

# Resend API key for the sign-in code emails
wrangler secret put RESEND_API_KEY

# Turnstile secret and the web review tool's JWT signing secret
wrangler secret put TURNSTILE_SECRET_KEY
wrangler secret put WEB_JWT_SECRET

# Recommended: a random pepper for sign-in code hashes (see below)
# node -e "console.log(require('crypto').randomBytes(32).toString('hex'))"
wrangler secret put LOGIN_CODE_PEPPER
```

To rotate: re-run the same command with a new value. Rotating
`LOGIN_CODE_PEPPER` (or setting it the first time) makes codes issued in the
previous 10 minutes stop verifying; people just request a new code.

`LOGIN_CODE_PEPPER` is optional. Without it, `login_requests.code` stores a plain
sha256 of the code, and codes are only about 40 bits, so a leaked table could be
brute-forced offline within a code's 10-minute life. With it, the column stores
HMAC-SHA256(pepper, code) and the table alone is useless.

There is no `ALLOWED_TOKENS` secret. If an old deployment still has one, delete
it (`wrangler secret delete ALLOWED_TOKENS`); the Worker ignores it.

## Deploy

```sh
npm run deploy
```

`npm run deploy` runs `scripts/guard-deploy.mjs` first, which refuses unless
the tree is clean, `HEAD` equals `origin/main` after a fetch, and
`npm run typecheck` and `npm test` pass. Do not run `npx wrangler deploy`
directly; it skips those checks.

The deploy URL will look like:
`https://revu-proxy.<your-cloudflare-subdomain>.workers.dev`

The Worker URL is compiled into the desktop app
(`src/Revu.Core/Services/RiotProxyEndpoint.cs`); renaming the Worker means
shipping a desktop build with the new URL.

## Smoke test

```sh
# health (unauthenticated)
curl https://revu-proxy.<sub>.workers.dev/health

# account lookup, with a desktop session token (sign in through the app, or
# POST /auth/login then /auth/verify) → { puuid, gameName, tagLine }
curl -H "Authorization: Bearer <session-token>" \
  "https://revu-proxy.<sub>.workers.dev/account?riotId=<gameName>%23<tagLine>&region=na1"

# recent match ids
curl -H "Authorization: Bearer <session-token>" \
  "https://revu-proxy.<sub>.workers.dev/matches?puuid=<PUUID>&region=na1&count=5&queue=420"

# one match
curl -H "Authorization: Bearer <session-token>" \
  "https://revu-proxy.<sub>.workers.dev/match/<MATCH_ID>?region=na1"
```

## Logs

```sh
wrangler tail
```

Each Riot passthrough request logs `{ token, userId?, path, status, ms }` as
JSON. `token` is the first 16 hex characters of sha256(token) (for web tool
calls, of the JWT's id), never the token itself; `userId` is present for desktop
calls. Sign-in failures log an 8-character hash of the email, never the address.

## Rate limits

The Riot key has one app-wide rate limit, so every upstream Riot call is charged
to a budget enforced by the `GlobalRateLimiter` Durable Object (one global
counter across all isolates). Only calls that actually reach Riot are charged:
a `/match` or `/timeline` served from the edge cache, or a request rejected by
validation, costs nothing.

| Var | Default | Meaning |
|-----|---------|---------|
| `AGGREGATE_RPS` | 18 | Hard cap for everyone, per second. Keep it under the key's real app-rate limit |
| `PER_TOKEN_RPS` | 2 | Per desktop session or web JWT, per second (per isolate) |
| `WEB_AGGREGATE_RPS` | a third of `AGGREGATE_RPS` | The most the anonymous web tool may use. Desktop may use the whole aggregate while web is idle, and always keeps the rest |
| `WEB_PER_IP_RPS` | 3 | Per address (IPv6 by /64) across every web JWT, per second |
| `WEB_SESSIONS_PER_IP` | 12 | Web JWTs minted per address per 30 minutes (a JWT lives 30 minutes) |

Adjust in `wrangler.toml` `[vars]`. If you upgrade to a higher-tier Riot key,
bump `AGGREGATE_RPS` (stay about 10% below Riot's ceiling). The Riot budget
fails OPEN if the Durable Object is unreachable (per-isolate gates and Riot's
own 429s remain). Addresses are hashed before they reach the Durable Object,
and its counters live in memory only.

Other limits (none of them touch the Riot budget):

- `POST /auth/login` and `POST /auth/verify`: 5 per minute per address per
  isolate, and 10 per minute per address globally (Durable Object, fails open).
  Each Durable Object namespace (auth, mint, view) holds at most 20,000 keys. A
  full namespace counts as unavailable, not as a deny, so a flood of fresh
  addresses can't lock everyone out: sign-in and web-session mints fail open,
  and the view counter fails closed (the view simply isn't counted).
- Sign-in emails: 3 per email per 15 minutes, and `EMAIL_DAILY_SEND_CAP` (default
  100) per UTC day across all users and both sign-in flows. Past the daily cap,
  both answer 503 `email_daily_cap` with a `Retry-After` to UTC midnight. Set it
  to what your Resend plan allows per day; 100 keeps a free plan's monthly quota
  safe even under a sustained attack.
- `POST /clips`: 30 per minute per user. `POST /clips/uploads`: 6 per minute per
  user. `POST /transcribe`: 30 per minute per user.
- `POST /clip-view/:id`: counts at most one view per address per clip per hour.

## Endpoints

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | `/health` | none | |
| GET | `/account` | session | `riotId=gameName%23tagLine`, `region=na1` → Riot account (puuid) |
| GET | `/matches` | session | `puuid`, `region`, `count?` (1 to 100), `queue?` → match ids |
| GET | `/match/:id` | session | `region` → match JSON (edge cached) |
| GET | `/timeline/:id` | session | `region` → match timeline JSON (edge cached) |
| GET | `/rank` | session | `puuid`, `region` → League-V4 entries |
| POST | `/auth/login` | none | `{ email }` → emails a code → `{ sent: true }` |
| POST | `/auth/verify` | none | `{ code, email? }` → `{ session_token, expires_at }` (see below) |
| POST | `/auth/logout` | session | Revokes the calling session only |
| POST | `/web/session` | none | `{ turnstileToken }` → `{ token, expiresIn }`, a 30-minute web tool JWT |
| GET | `/web/account`, `/web/matches`, `/web/match/:id`, `/web/timeline/:id`, `/web/rank` | web JWT | Same as the desktop routes |

`region` is a platform id (`na1`, `euw1`, `kr`, `oc1`, ...; 17 in all). Match-V5
goes to its regional cluster (americas, asia, europe, sea). Account-V1 has no sea
host, so OCE and SEA platforms use asia for it (Riot documents americas, asia and
europe for Account-V1, each answering for every account). League-V4 is
platform-routed. A Riot call whose response headers take longer than 10 s is
504 `riot_timeout` (the body itself is not timed, so a large timeline still
streams to a slow reader); any other upstream failure is 502 `proxy_error`. A
session lookup that D1 can't answer is 503 `auth_unavailable`, never a 401. Both
carry a `message` sentence, which the desktop shows as is. Anything unexpected
is a JSON 500 `internal` with CORS headers.

### Desktop sign-in

- `POST /auth/login` stores only a hash of the 8-character code (10 minutes,
  single use). A failed email send deletes its row and refunds the daily count,
  so it never throttles a retry.
- `POST /auth/verify` with `{ email, code }` is bound to the email. `{ code }`
  alone is still accepted for desktop builds that don't send the email yet. Set
  `REQUIRE_VERIFY_EMAIL = "1"` once desktop builds that send the email are the
  norm. Code-only verify then answers 400 `email_required`, and every bound
  attempt first adds 1 to `attempts` on that email's live desktop codes, so a
  code is consumed only while `attempts <= 5`. Until then bound attempts are not
  counted: a guesser would simply use the uncounted code-only path, so counting
  would protect nothing and would only let anyone who knows an email burn that
  user's codes. Neither path consumes a code whose `attempts` is above 5.
- The first successful verify for a new email creates the account.
- Web codes (purpose `web`) are refused here; desktop codes are refused at
  `/auth/web/email/verify`.

## Clip sharing and transcripts

Owner routes need `Authorization: Bearer <session token>` from a real account
session (D1). A missing or bad token gets 401: `unauthorized` on the 3.14.0
routes (uploads, parts, complete, transcripts, `/transcribe`); the older routes
keep their historical codes (`missing_bearer`, `empty_token`, `invalid_token`).
A session lookup D1 can't answer is 503 `auth_unavailable`. Errors are JSON
`{ error, message? }`. Content-Type checks compare the media type only, so
`text/plain; charset=utf-8` is fine.

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| POST | `/clips?title&champion&duration&narrated=0\|1` | owner | Legacy single-request upload (desktop uses it up to 95 MiB). `Content-Length` required (411 `length_required` without it), at most 100 MB. The body goes straight to R2; then one 16-byte ranged read checks the video magic bytes, and a mismatch is deleted and 415 `unsupported_media_type`. 30 per minute per user. Over the storage budget: 403 `quota_exceeded` with `reason: "storage_full"`. 201 `{ id, url, expires_at }` |
| POST | `/clips/uploads?title&champion&duration&size&narrated=0\|1` | owner | Start a multipart upload (`video/mp4` or `video/webm`, empty body). duration 1 to 610 s, size up to 2 GiB. 201 `{ id, part_size, part_count, expires_at }`. 6 per minute per user, at most 2 pending uploads. Over the storage budget: 403 `storage_full` as above |
| PUT | `/clips/:id/parts/:n` | owner | Raw part bytes. `Content-Length` required and exact (`part_size`, last part is the remainder). At most 2000 part PUTs per account per UTC day across every upload (a D1 counter that row deletes don't reset), then 429 `daily_part_limit` (`retry_at`, `Retry-After`). At most `2 x part_count + 4` PUTs per upload, then 429 `too_many_part_uploads`. 200 `{ part_number, etag }` |
| POST | `/clips/:id/complete` | owner | `{ parts: [ { part_number, etag } ] }`. Idempotent. 201 `{ id, url, expires_at }` |
| DELETE | `/clips/:id` | owner | Clips and full-game shares alike. Claims the row (expired, `purging`), aborts a pending upload, deletes the R2 object, then the transcript, VOD meta and row. If the abort really fails (anything but "upload already gone") or the R2 delete fails, the claimed row stays (still counted in the storage budget) and the hourly purge retries it; the answer is still 200 `{ ok: true }` |
| GET | `/clips/mine` | owner | Own clips and full-game shares, including `status`, `narrated`, `has_transcript`, `kind` (`clip` or `vod`), `quality` and `has_notes`. VOD items link to `${WATCH_BASE}/v/<id>` |
| POST | `/transcribe?offset_ms&duration_ms&language=en` | owner | `text/plain` base64 of a mono 16 kHz 24 kbps MP3 chunk (at most 1 MiB, `Content-Length` required). Whisper via Workers AI. Charged the larger of `duration_ms` and the length the body implies at 24 kbps; a body far longer than `duration_ms` is 400. 1200 audio s per user per day (a ninth of the global cap, so a few accounts can't switch transcripts off for everyone), 10800 s across all users, 30 requests per minute per user |
| PUT | `/clips/:id/transcript` | owner | Version 1 transcript document (at most 256 KiB, 3000 segments; 8 MiB of transcripts per account across live clips). Zero segments removes it. The VTT is generated on read |
| DELETE | `/clips/:id/transcript` | owner | Idempotent |
| GET | `/clip-meta/:id` | public | Adds `narrated`, `has_transcript`, `transcript_language` |
| GET, HEAD | `/clip-file/:id` | public | Video bytes. One `bytes=` Range is a 206; a range past the end is 416; If-None-Match is honored (304) and so is If-Range; HEAD reads metadata only |
| POST | `/clip-view/:id` | public | Counts a view, at most once per address per clip per hour. Always 200 `{ ok: true }` |
| GET, HEAD | `/clip-transcript/:id[?format=vtt]` | public | JSON `{ id, language, segments }` or WebVTT |

Pending (still uploading) clips are 404 on every public route. Clips expire 3 days
after upload (multipart: 3 days after complete). The active quota is 150 clips and
12 GiB per user, pending uploads included.

### Bindings

`wrangler.toml` binds D1 (`DB`), R2 (`CLIPS`), the rate limiter Durable Object and
Workers AI:

```toml
[ai]
binding = "AI"
```

Without the `AI` binding, `/transcribe` returns 503 `transcribe_unavailable`.

### Migration 0003

`migrations/0003_clip_uploads_transcripts.sql` adds the multipart and transcript
columns to `clips` plus the `clip_transcripts` and `transcribe_usage` tables.
Apply it before deploying the Worker that uses it:

```sh
# 1. See what D1 thinks is applied.
npx wrangler d1 migrations list revu-db --remote

# 2a. If 0001/0002 were originally applied with `d1 execute` (they show as
#     unapplied even though the tables exist), apply 0003 alone:
npx wrangler d1 execute revu-db --remote --file=migrations/0003_clip_uploads_transcripts.sql

# 2b. Otherwise:
npx wrangler d1 migrations apply revu-db --remote
```

### Cron

The scheduled handler runs hourly (`0 * * * *`). In order it settles quiet HLS
shares (see [HLS shares](#hls-shares-watch-while-it-uploads)), purges expired
clips, full-game shares and expired pending uploads (aborting the upload and
deleting the R2 object, the transcript and the VOD meta), retries
`storage_tombstones`, then expired sessions, then `login_requests` rows that
expired over an hour ago (with their email addresses, used or not), then
`transcribe_usage`, `email_send_usage`, `vod_share_usage` and
`upload_part_usage` rows older than 7 days. Last, it discards at most 5
pending uploads of any account with no part activity for 30 minutes (so
abandoned reservations stop holding the storage pool). Each discard costs
about 6 D1 and R2 calls, so the sweep runs last and small: a backlog can't use
up the run's D1 query budget on Workers Free before the purge, and later runs
clear the rest. A row or tombstone whose upload abort really failed is kept
for the next run (its parts may still be in R2) until it is 8 days old, past
R2's own 7-day rule for incomplete uploads.

### Migration 0005 and deploy order

`migrations/0005_email_send_usage.sql` is additive: one `email_send_usage` table
(a count per UTC day, no addresses) behind the daily sign-in email ceiling. Apply
it before deploying the Worker that uses it. If it is missing, the ceiling fails
open (sign-in keeps working, uncapped) and logs `email_budget.reserve` errors.

```sh
cd proxy
npx wrangler d1 migrations list revu-db --remote
npx wrangler d1 migrations apply revu-db --remote
npm run deploy                        # guarded, see Deploy above
```

### Optional R2 lifecycle rule

As an extra safety net for abandoned multipart uploads, an R2 lifecycle rule on
`revu-clips` can abort incomplete multipart uploads after 1 day
(`wrangler r2 bucket lifecycle add`). It is not required: the Worker auto-aborts
idle uploads on the owner's next init and the cron aborts expired ones.

## Full-game (VOD) shares

A whole game's recording, re-encoded by the desktop app (H.264, about 35 MB per
minute at 1080p), shared as `https://revu.lol/v/<id>` for 3 days. VOD rows live
in `clips` with `kind = 'vod'` and R2 key `vods/<id>.mp4`, so the hourly purge,
owner delete (`DELETE /clips/:id`), account deletion and the storage budget
handle them like every other stored object. Clip routes never serve or change a
VOD row, and VOD routes never touch a clip row.

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | `/vod-shares/status` | owner | `allowed`, `sharing`, `serving`, `now`, own `shares`. Allowlisted accounts also get `limits`, `usage` (`live`, `pending`, `starts_today`, `day_resets_at`) and `pool` (budgets, used bytes, `free_bytes`, `releases`). 30 per minute per user |
| POST | `/vod-shares/uploads?size&duration&quality=1080p\|720p` | owner | `video/mp4`, empty body. size up to 2 GiB, duration 1 to 7200 s. Refusals, in order: 503 `vod_sharing_disabled`, 403 `not_allowed`, 429 `rate_limited` (6 per minute), 415, 400, 413, then 429 `too_many_pending_uploads`, 409 `live_limit`, 409 `pool_full` (`retry_at`, `free_bytes`), 429 `daily_limit` (`retry_at`, `Retry-After`). 201 `{ id, part_size, part_count, expires_at, share_expires_at, url }` |
| PUT | `/vod-shares/:id/parts/:n` | owner | Same as the clip part route (16 MiB parts) |
| POST | `/vod-shares/:id/complete` | owner | Same as clip complete. The share expires 3 days after INIT (`created_at` is never rewritten). Idempotent. 201 `{ id, url, expires_at }` |
| PUT | `/vod-shares/:id/markers` | owner | Markers document v1 (at most 64 KB; markers 48 KiB, notes 14 KiB). Validated and normalized by `src/vod-markers.ts`. Sets the public title and champion. Accepted for a ready share and for one still uploading inside its upload window (the desktop sends it right after init), so the watch page can name the game while it uploads. 200 `{ ok, markers, notes }` |
| GET | `/vod-meta/:id` | public | Ready: `{ id, kind, content_type, quality, duration_s, size_bytes, created_at, expires_at, serving, play_limited, meta, now }`. Still uploading (inside its upload window): `{ id, kind, status: "uploading", quality, duration_s, created_at, expires_at, serving, progress: { parts, part_count }, stalled, meta, now }`, where `parts` is the accepted part PUTs capped at `part_count` (an estimate: in-flight and retried parts count), `expires_at` is when the share will end, `stalled` means no part for 15 minutes, `meta` is the markers document once attached (else null) and `now` is the server's unix time. HLS shares add their fields (see below). `no-store`, `X-Robots-Tag: noindex, nofollow` |
| GET, HEAD | `/vod-file/:id` | public | Ready MP4 shares only (404 while uploading, and always for an HLS share). Answers only on the host of `PUBLIC_BASE` (404 on workers.dev). Same range, conditional and HEAD handling as `/clip-file`, `Cache-Control: private, max-age=3600`, noindex. 503 `vod_serving_disabled` when serving is off (var or ops flag), 429 `play_limit` over the daily cap |

Limits (all in `src/share-limits.ts`):

- Only accounts with a `vod_sharers` row can start a share. Rows are added by hand
  at deploy time, never by code.
- Per account: 3 live shares (ready, or uploading and not idle for 15 minutes),
  1 pending upload, 6 starts per UTC day (a D1 counter; a start is spent at init
  and not refunded). Idle VOD uploads are discarded by the owner's next init.
- Upload window: long enough to send the declared size at 0.45 Mbps plus an
  hour, between 6 and 12 hours (a 2 GiB share gets about 11.6 hours). The share
  still expires 3 days after init.
- Part PUTs per row (clips too): at most `2 x part_count + 4`, then 429
  `too_many_part_uploads`.
- Play cap: 200 plays per VOD per UTC day. A play is a GET with no Range, an
  ignored Range or a Range starting at byte 0. Over the cap every request for
  that VOD (seeks included) is 429 until UTC midnight. A client that never asks
  for byte 0 (for example `Range: bytes=1-`) is not counted; it is refused only
  once counted plays reach the cap.

### Storage budget

`TOTAL_BUDGET_BYTES` (default 9e9) and `VOD_BUDGET_BYTES` (default 7e9, clamped
to the total) in `wrangler.toml [vars]` cover every stored object: clips and
VODs in every state (uploading reservations at their declared size, ready,
purging) plus `storage_tombstones`. Expired rows count until the purge removes
them. Both clip upload paths and VOD init check it before reserving and again
after their row is committed; the loser of a race rolls itself back. A clip
upload over the total is 403 `{ error: "quota_exceeded", reason: "storage_full",
message }` (4.0.1 desktops show the message); a VOD init over either budget is
409 `pool_full`. The single-request clip upload can't reserve before streaming,
so concurrent ones may overshoot by their own bytes (each at most 100 MB) until
the post-insert recount rolls them back. Clips can use the whole total, so a
clip-heavy period can leave VODs `pool_full` while the VOD subset has room.

One account outside `vod_sharers` may hold at most `USER_CLIP_BUDGET_BYTES`
(default 3e9, a third of the total) of clips, pending reservations included,
so no single account can fill the pool; over it is the usual 403
`quota_exceeded`. A clip upload or VOD init that would be refused for storage
first discards up to 5 idle pending uploads (30 minutes without part activity)
of any account.

When an R2 delete or upload abort fails, the bytes stay counted: owner delete,
discards and the single-request rollbacks keep the row as an expired `purging`
row, and account deletion (and a rollback with no row left) writes
`storage_tombstones` rows. The hourly purge retries both.

### Kill switches

`VOD_SHARING = "0"` (or `"off"`) refuses new VOD starts (parts and complete of an
upload already started still work). `VOD_SERVING = "0"` (or `"off"`) stops VOD
playback. Anything else, or unset, is on. Playback can also be stopped without
a deploy through the `ops_flags` table (see the HLS kill-switch runbook below).

### Migration 0006 and deploy order

`migrations/0006_vod_shares.sql` is additive: `kind`, `part_puts`, `quality`,
`play_day` and `play_count` columns on `clips` (existing rows become kind
`clip`), plus the `vod_sharers`, `vod_share_usage`, `upload_part_usage`,
`vod_share_meta` and `storage_tombstones` tables. Apply it before deploying the
Worker that uses it. 4.0.1 desktops keep sharing clips against the new Worker;
the only changes they can see are the `storage_full` refusal, the per-account
share of the pool (shown as their usual clip quota message) and the daily part
budget.

```sh
cd proxy
npx wrangler d1 migrations list revu-db --remote
npx wrangler d1 migrations apply revu-db --remote
npm run deploy                        # guarded, see Deploy above

# Allowlist an account (manual, by the operator only):
npx wrangler d1 execute revu-db --remote --command \
  "INSERT OR IGNORE INTO vod_sharers (user_id, added_at) SELECT id, unixepoch() FROM users WHERE email = '<email>'"
```

The bucket's 3-day lifecycle rule (all prefixes) matches the VOD TTL, which is
counted from init; no lifecycle change is needed.

For local runs: `npx wrangler d1 migrations apply revu-db --local`, then
`npx wrangler dev` with a gitignored `.dev.vars` setting `AUTH_BASE`,
`PUBLIC_BASE` to `http://localhost:8787` and `WATCH_BASE` to
`http://localhost:8788`. The tests run the VOD routes against an in-memory
SQLite database with every migration applied (`tests/sqlite-d1.ts`).

## HLS shares (watch while it uploads)

A full-game share can also be stored as HLS: 2-second fMP4 segments (one GOP
each), one R2 object per segment (`vods/<id>/<seq>.m4s`), with each run's init
segment in D1. Viewers play a growing EVENT playlist from 0:00 while the rest
uploads. It is **off by default** (`VOD_HLS = "0"`) and needs Workers Paid: one
full view costs about 1,200 Worker requests. The desktop asks for it with
`format=hls` only when `GET /vod-shares/status` says `hls.enabled`; an older
proxy ignores `format` and the desktop shares the MP4 way on the row it got.

HLS rows live in `clips` (`kind = 'vod'`, `format = 'hls'`, `r2_key` the
prefix `vods/<id>/`, `part_count` the segment slots, `size_bytes` the
reservation) plus `vod_hls` (state), `vod_hls_runs` (inits) and
`vod_hls_segments` (one row per claimed segment). MP4 routes never touch an HLS
row and HLS routes never touch an MP4 row or a clip.

### Upload routes (desktop, Bearer, allowlisted owner)

| Method | Path | Notes |
|--------|------|-------|
| GET | `/vod-shares/status` | Allowed sharers also get `hls: { enabled, segment_seconds: 2, target_duration: 6, max_segment_bytes: 16777216, init_max_bytes: 16384, playable_ms: 8000, max_runs: 8 }`. Every `shares[]` row gets `format` (`hls` or `mp4`); HLS rows also `segments` (committed) and `paused` |
| POST | `/vod-shares/uploads?size&duration&quality&fps&format=hls` | The MP4 init's checks in the same order; `format` other than `mp4` or `hls` is 400, and `hls` while HLS is off for the caller is 409 `hls_unavailable`, both before any idle discard, limit, pool check or daily start. One D1 batch, no R2 call. `part_count` = ceil(d/2) + 2 + max(10, ceil(0.01 d)) slots. 201 `{ id, format: "hls", part_count, segment_seconds, target_duration, max_segment_bytes, init_max_bytes, playable_ms, max_runs, expires_at, share_expires_at, url }` |
| PUT | `/vod-shares/:id/hls/runs/:k?from&quality&fps` | Body: run k's init (1 to 16,384 bytes starting with `ftyp`; 411, 413, 415). Run 0 starts at 0; run k >= 1 at a seq past run k-1's start with no committed segment at or after it. A replay of the last run with identical bytes and `from` is 200 `already`; anything else that doesn't follow is 409 `run_conflict`; k >= 8 is 429 `too_many_runs`. Sets `quality`, counts one attempt. 200 `{ k, from, quality }` |
| PUT | `/vod-shares/:id/hls/seg/:seq?ms&run` | Body: one segment, 1 byte to 16 MiB (411, 413). `ms` 1 to 6,499 (400 `bad_duration`), `seq` below `part_count` (400 `bad_segment_number`), run `run` must cover `seq` (409 `run_mismatch`). One claim batch checks the session too, then `env.CLIPS.put(key, request.body)` and the commit batch run as one task handed to `ctx.waitUntil`. A committed seq is 200 `already` and writes nothing. Over the reservation it grows by max(64 MiB, 2%) up to 2 GiB while the pool has room, else 413 `over_reservation`. At most `2 x part_count + 16` attempts (runs included), then 429 `too_many_part_uploads`; not counted in the daily part budget. A share stopped meanwhile: the object is deleted (or tombstoned) and 404. 200 `{ seq, committed, media_ms, playable }` with `Server-Timing: d1;dur=N, r2;dur=N` |
| POST | `/vod-shares/:id/hls/pause` | `{ "reason": "game" }`. Idempotent; the next segment or run PUT clears it. A paused share keeps its pending slot and is never idle. 200 `{ paused: true, reason }` |
| POST | `/vod-shares/:id/hls/end` | `{ segments: N, truncated }`. Needs run 0 and at least N >= 1 committed segments (409 `segments_incomplete` with `committed`). Ends at E = the committed count: rows and objects at or after E go, the bytes are recounted from the rows and inits, the share is ready for 3 days from init. A replay gets the same 201 `{ id, url, expires_at, size_bytes, media_ms, segments, truncated }` |

`DELETE /clips/:id` (Stop sharing) and `PUT /vod-shares/:id/markers` work as
for MP4 shares.

### Public routes (only on the host of `PUBLIC_BASE`)

Any other host, workers.dev included, gets 404. Every response carries
`nosniff`, `X-Robots-Tag: noindex, nofollow`,
`Cross-Origin-Resource-Policy: same-site` and `Vary: Origin` (CORS for
`ALLOWED_ORIGINS` as everywhere), and never `public` or `s-maxage`. A request
with `Sec-Fetch-Site: cross-site` from an origin outside `ALLOWED_ORIGINS` is
403 before any D1 or R2 work. Serving off (var or ops flag) is 503
`vod_serving_disabled`.

| Method | Path | Notes |
|--------|------|-------|
| POST | `/vod-play/:id` | Needs an `Origin` in `ALLOWED_ORIGINS` (403 `forbidden_origin`). The only place an HLS play counts: 200 per share per UTC day, then 429 `play_limit` with `Retry-After` to UTC midnight. Not yet playable: 404 `not_ready`, nothing counted. 200 `{ token, expires_at }` (12 hours), `no-store` |
| GET, HEAD | `/vod-hls/:id/:token/index.m3u8` | The EVENT playlist (ENDLIST once ended), built from committed rows. 404 `not_ready` until playable (8 s committed with run 0, or ended with a segment). `no-store` |
| GET, HEAD | `/vod-hls/:id/:token/init-:k.mp4` | Run k's init. `private, max-age=3600`, `CSP: sandbox` |
| GET, HEAD | `/vod-hls/:id/:token/:seq.m4s` | Committed segments only, with Range, 206 and conditional requests. `private, max-age=3600`, `CSP: sandbox` |

The token is `<exp>.<sig>`: sig is the first 22 characters of
base64url(HMAC-SHA256(`VOD_PLAY_SECRET`, `<id>.<exp>`)). It is checked first,
CPU only; a bad or expired one is 403 `bad_token` and the page mints another.
A reached cap never stops people already watching. Share state is cached per
isolate for 1 s (playlist) or 2 s (init, segment), so Stop sharing reaches
every viewer within 2 seconds; what a browser already downloaded can't be
recalled, as with `/vod-file`.

`/vod-meta/:id` adds for HLS shares `format`, `playable`, `media_seconds`,
`ingest_x` (media seconds uploaded per wall second, an EWMA; null before two
commits), `last_commit_age_s`, `paused_reason`, `ended`, `truncated`,
`play_limited`, and `progress.parts` = committed segments. `duration_s` is the
declared length while uploading and the media's after the end.

Playlist (LF line ends; a resume run k gets a `DISCONTINUITY`, and a new
`EXT-X-MAP` only when its init differs from run k-1's):

```
#EXTM3U
#EXT-X-VERSION:7
#EXT-X-TARGETDURATION:6
#EXT-X-MEDIA-SEQUENCE:0
#EXT-X-PLAYLIST-TYPE:EVENT
#EXT-X-INDEPENDENT-SEGMENTS
#EXT-X-START:TIME-OFFSET=0,PRECISE=YES
#EXT-X-MAP:URI="init-0.mp4"
#EXTINF:2.000,
0.m4s
...
#EXT-X-ENDLIST
```

It updates faster than half the target duration and starts within three
target durations of a live edge, both against RFC 8216 and both on purpose:
only hls.js plays it while it grows.

### Cleanup

Every delete path (discard, Stop sharing, the hourly purge, account deletion,
the cron settle) claims the row first, deletes the keys `0..max_seq` by name
(1000 per call, never a list), then in one batch deletes the rows and writes a
prefix tombstone `vods/<id>/` for keys `0..max_seq+8` that the purge deletes
once more after 10 minutes, once PUTs in flight have settled. A failed R2
delete keeps the claimed row (or the tombstone) counted in the storage budget
and the hourly purge retries it, inside a budget of 40 R2 calls per run.

The cron (and the owner's next VOD init) settles a share that went quiet: idle
for 30 minutes (15 for the owner's own init) and not paused, paused for 6
hours, or past its upload window. A watchable one is ended at its edge as
truncated; any other is discarded. The idle sweep never touches HLS shares.

### Switches and secret

- `VOD_HLS` (var, default `"0"`): `"1"` allows new HLS starts for every
  allowlisted sharer, `"users:<id>,<id>"` for those accounts only. It gates new
  starts only.
- `VOD_PLAY_SECRET` (secret, at least 32 characters): signs play tokens. Unset,
  no HLS starts and HLS playback is 503. Rotating it ends every token in
  flight; pages mint a new one (one more counted play each).
- `ops_flags` (D1): `vod_serving = '0'` stops every public VOD route (HLS and
  MP4) and `vod_hls = '0'` stops new HLS starts, within 30 seconds (per-isolate
  cache), with no deploy. No row: the var decides.

### Kill-switch runbook

```sh
cd proxy
# Stop all VOD playback (HLS and MP4) within 30 s:
npx wrangler d1 execute revu-db --remote --command \
  "INSERT INTO ops_flags (name, value) VALUES ('vod_serving', '0') ON CONFLICT (name) DO UPDATE SET value = excluded.value"
# Stop new HLS shares only (desktops fall back to MP4):
npx wrangler d1 execute revu-db --remote --command \
  "INSERT INTO ops_flags (name, value) VALUES ('vod_hls', '0') ON CONFLICT (name) DO UPDATE SET value = excluded.value"
# Back to what wrangler.toml says:
npx wrangler d1 execute revu-db --remote --command "DELETE FROM ops_flags WHERE name IN ('vod_serving', 'vod_hls')"
```

### WAF rule (before turning HLS on)

One rate-limiting rule on the clips.revu.lol zone: requests whose path starts
with `/vod-hls/`, `/vod-play/` or `/vod-file/`, counted per IP, at most 200 per
10 seconds, then block. A viewer makes about 2 requests per 10 seconds.
Together with tokens, the play cap and the billing notifications ($10 and
$25), it bounds the cost of a misbehaving client; Workers Paid has no hard cap.

### Logs

`[observability]` keeps 10% of what the code logs (structured JSON). Invocation
logs are off because they record request URLs, and a media URL carries a play
token; no token, cookie or raw IP is logged, and error logs replace the token
in paths with `-`.

### Migration 0007, deploy order and rollback

`migrations/0007_vod_hls.sql` is additive: `clips.format` and
`clips.hls_bytes`, the `vod_hls`, `vod_hls_runs`, `vod_hls_segments` and
`ops_flags` tables, and `key_count` and `not_before` on `storage_tombstones`.
Existing rows keep `format` NULL.

```sh
cd proxy
npx wrangler secret put VOD_PLAY_SECRET           # at least 32 characters
npx wrangler d1 migrations list revu-db --remote
npx wrangler d1 migrations apply revu-db --remote  # 0007
npm run deploy                                     # guarded; VOD_HLS stays "0"
# Later, once the site's HLS player is live: VOD_HLS = "users:<owner id>" for a
# canary day, then "1" (each a wrangler.toml change and a guarded deploy).
```

With `VOD_HLS = "0"` the visible changes are: `/vod-file` answers on
clips.revu.lol only, `/vod-meta` carries `now`, and `/vod-shares/status`
carries `hls` (`enabled: false`) and `shares[].format`.

Rollback: set `VOD_HLS = "0"` (or the `vod_hls` flag) and new shares are MP4
again, while HLS shares already uploading keep uploading and serving.
**Never roll the Worker back below the HLS release while HLS rows exist**: older
code deletes single keys (orphaning every segment) and 404s the watch pages.
Turn HLS off first, let the HLS rows expire (3 days) or delete them, then roll
back.

Local dev: set `PUBLIC_BASE=http://localhost:8787`, `VOD_HLS=1` and a
`VOD_PLAY_SECRET` in `.dev.vars`, apply the migrations with `--local`, and
`npx wrangler dev`. `tests/hls-e2e.test.ts` runs real fMP4 media through these
routes with ffmpeg as the HLS client when `HLS_MEDIA_DIR`, `FFMPEG` and
`FFPROBE` are set.

## Web accounts (revu.lol)

The site's sign-in, My clips and Account pages (`revu.lol/sign-in`,
`/my-clips`, `/account`) call a cookie-authenticated API on this Worker under
`https://clips.revu.lol/auth/web/*`. Sign-in is by emailed one-time code only.
The first successful verify for a new email creates the account, and the same
account works in the desktop app.

### Routes

Errors are JSON `{ error }` (plus `retry_after` on a 429). Every response,
errors and the preflight included, carries `Vary: Origin` and
`Cache-Control: no-store`.

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | `/auth/web/providers` | none | `{ providers: ["email"] }`, or `[]` when Turnstile or Resend is not fully configured |
| POST | `/auth/web/email/start` | none | `{ email, turnstileToken }`. Strict Turnstile, then an 8-character code (10 min, single use, purpose `web`). Always `{ sent: true }`, whether or not an account exists. 5/min/IP |
| POST | `/auth/web/email/verify` | none | `{ email, code }`. Sets the session cookie. `{ ok, new_account, user: { email, created_at } }`. 10/min/IP |
| GET | `/auth/web/me` | cookie | `{ user, identities, session: { expires_at } }` |
| GET | `/auth/web/clips` | cookie | Same clip shape as `/clips/mine` |
| DELETE | `/auth/web/clips/:id` | cookie | Another user's clip is 404, like a missing one |
| POST | `/auth/web/logout` | optional cookie | Ends this browser's session; always clears the cookie |
| POST | `/auth/web/logout-all` | cookie | Ends every web session of the user. The desktop stays signed in |
| POST | `/auth/web/account/delete` | cookie | `{ email }` must match the account email. Deletes everything (see below) |

Everything except start and verify shares 60/min/IP (429 `rate_limit_auth`).
These limits are in memory per isolate: they slow one client, they are not a
durable global limit.

### Cookie, CORS and CSRF

- Cookie: `__Host-revu_web=<48 hex>; Path=/; Secure; HttpOnly; SameSite=Lax;
  Max-Age=2592000`, host-only on clips.revu.lol. revu.lol and clips.revu.lol
  share the registrable domain, so the cookie is first-party for fetches from
  revu.lol pages. The token never appears in JSON, logs or page JS.
- `SameSite=Lax` (not Strict) is deliberate: Safari can withhold Strict cookies
  after a cross-site arrival (a revu.lol/my-clips link clicked in Discord).
  CSRF is enforced by the gates below; Lax is defense in depth.
- Host pin: `/auth/web/*`, preflight included, only answers on the host of
  `AUTH_BASE`. Anywhere else (including `*.workers.dev`, which is on the Public
  Suffix List and is what the desktop app talks to) it is 404, OPTIONS is a bare
  204, and no cookie is ever set.
- Gates, in order: host pin; `Origin` must exactly match one of
  `CREDENTIALED_ORIGINS` (missing or `null` is rejected); `Sec-Fetch-Site`, when
  present, must be `same-site` or `same-origin`; every non-GET must send
  `X-Revu-CSRF: 1` (which forces a preflight); then the per-IP limit; bodies
  must be `application/json` objects of at most 4096 bytes.
- Credentialed CORS is path-scoped to `/auth/web/*` and uses
  `CREDENTIALED_ORIGINS`, not `ALLOWED_ORIGINS` (which includes localhost).
  Every other path's CORS output is unchanged.
- The clip Open Graph page (`clips.revu.lol/<id>`) shares the cookie origin, so
  it runs no script and sends a `default-src 'none'` CSP.

### Web vs desktop sessions

Both live in `sessions`. Web rows have `kind = 'web'` and store
`sha256Hex("web:" + token)`; desktop rows keep the column default `'desktop'`
and the plain `sha256Hex(token)`. The prefix makes the two mutually unusable: a
web token sent as a Bearer and a desktop token sent as the cookie both fail.
The cookie is read only inside the `/auth/web/*` dispatcher, and Bearer auth
never accepts it. Web sign out and sign out everywhere never touch desktop rows;
only account deletion does. A verify deletes the web session of the cookie the
browser already holds before creating the new one. Sessions last 30 days,
absolute (no sliding).

Desktop `/auth/verify` refuses purpose `web` codes with its existing 400, and
after a successful verify it upserts the email identity (best effort).

### Web codes and Turnstile

- Codes are issued only after Turnstile passes strictly: `success`, action
  `web_signin`, and a hostname in `TURNSTILE_HOSTNAMES`, sent with `remoteip`
  and a 10 s timeout. Any network error fails closed. An empty
  `TURNSTILE_HOSTNAMES` makes `providers` empty and start 503
  `sign_in_unavailable`.
- Dev relax: `TURNSTILE_DEV_RELAX=1` skips the action and hostname checks, and
  only while `AUTH_BASE` starts with `http://localhost:`, so it can never relax
  production. Keep it out of `wrangler.toml`.
- One atomic conditional insert enforces both per-email caps: a 60 s cooldown
  between web codes (429 `retry_after: 60`) and at most 3 codes of any purpose
  per 15 min (429 `retry_after: 900`). Desktop codes never trigger the 60 s
  cooldown. A failed Resend send deletes its row so an immediate retry is not
  throttled, and Resend errors are logged by status only.
- Verify is bound to the email. Every attempt first adds 1 to `attempts` on all
  live web codes for the email, then consumes a matching code only while
  `attempts <= 5`, so at most 5 guesses are evaluated per code.
- Codes use rejection sampling (bytes below 248) over a 31-character alphabet.

- Every code email (web and desktop) counts toward the global
  `EMAIL_DAILY_SEND_CAP`; past it, start answers 503 `email_daily_cap` with
  `retry_after` to UTC midnight.

Accepted risks (documented, not fixed):

- Lockout: anyone who knows an email can burn its live web codes with 5 bad
  guesses (its desktop codes too, once `REQUIRE_VERIFY_EMAIL = "1"`), or use up
  the 3-per-15-minutes budget through the desktop `/auth/login` (which can't
  run Turnstile). The workaround is to wait 15 minutes or sign in from the
  other surface.
- Email bombing and quota burn through `/auth/login`: bounded by the per-email
  cap, the global per-address limit and the daily ceiling, which pauses sign-in
  emails for everyone until UTC midnight once reached. That outage is the price
  of protecting the provider quota; alerting on `email_budget.cap` log lines is
  a follow-up.

### Account deletion

`POST /auth/web/account/delete` with the typed account email. R2 goes first
(cascades never reach object storage): pending multipart uploads are aborted
(at most 20), then every clip and VOD object is deleted in chunks of 1000 (HLS
shares are claimed first and their segment keys deleted by name; each leaves a
delayed prefix tombstone, see [Cleanup](#cleanup)). Then
one D1 batch first copies every row whose R2 delete or abort failed (or whose
upload was past the abort cap) into `storage_tombstones` with its upload id
(still counted in the storage budget, retried by the hourly purge), then
deletes the transcripts, VOD share meta, clips, every session (web AND
desktop), identities, `transcribe_usage`, `vod_share_usage`,
`upload_part_usage` and `vod_sharers` rows, `login_requests` for the email, and the user. The log line
carries the user id and the clip and tombstone counts, never the email. The desktop app is
signed out on its next server call; clips on the PC are untouched.

### Migration 0004 and deploy order

`migrations/0004_web_accounts.sql` is additive: `sessions.kind`,
`login_requests.attempts`, and the `identities` table (an email identity is
backfilled for every user, with `subject` = SQL `lower(email)`). Identities are
informational in v1; sign-in still keys on `users.email`.

Apply the migration BEFORE deploying the Worker (otherwise the web routes 500;
desktop `/auth/verify` also reads `login_requests.attempts` now, so it needs
0004 too), then deploy the site:

```sh
cd proxy
npx wrangler d1 migrations apply revu-db --remote
npx wrangler d1 migrations list revu-db --remote
npm run deploy                        # guarded, see Deploy above
cd .. && node scripts/deploy-site.mjs # then the site, also guarded
```

The hourly cron also deletes `login_requests` rows that expired over an hour
ago (the margin keeps rows the 15-minute throttle still counts).

### Local dev

Put overrides in the gitignored `proxy/.dev.vars` (never commit it):

```
AUTH_BASE=http://localhost:8787
CREDENTIALED_ORIGINS=http://localhost:8788
TURNSTILE_DEV_RELAX=1
TURNSTILE_SECRET_KEY=<Cloudflare's documented always-pass test secret>
RESEND_API_KEY=<your own Resend key>
```

```sh
npx wrangler d1 migrations apply revu-db --local
npx wrangler dev            # Worker on http://localhost:8787
# site: `cd site && npx wrangler pages dev . --port 8788` (site/README.md)
```

Use `localhost`, never `127.0.0.1`, for both. Use Chrome or Firefox, which
accept `Secure` (and `__Host-`) cookies on `http://localhost`.

### Future providers (design notes, not built)

- An `oauth_states` table (state hash, provider, intent, link user, PKCE
  verifier, nonce, return_to, expiry), authorization code with PKCE S256, state
  and nonce, a short-lived `__Host-revu_oauth` Lax cookie, and callbacks at
  `/auth/web/{provider}/callback`. `providers` turns each button on.
- Riot-only users would get a placeholder email (`riot-<hex>@noemail.invalid`
  plus a `users.email_placeholder` flag), so `users` needs no rebuild.
- `identities` has no `UNIQUE(user_id, provider)`, so several Riot accounts can
  link to one user; only email is limited to one per user (a droppable partial
  index).
