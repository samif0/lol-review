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
| POST | `/clips?title&champion&duration&narrated=0\|1` | owner | Legacy single-request upload (desktop uses it up to 95 MiB). `Content-Length` required (411 `length_required` without it), at most 100 MB. The body goes straight to R2; then one 16-byte ranged read checks the video magic bytes, and a mismatch is deleted and 415 `unsupported_media_type`. 30 per minute per user. 201 `{ id, url, expires_at }` |
| POST | `/clips/uploads?title&champion&duration&size&narrated=0\|1` | owner | Start a multipart upload (`video/mp4` or `video/webm`, empty body). duration 1 to 610 s, size up to 2 GiB. 201 `{ id, part_size, part_count, expires_at }`. 6 per minute per user, at most 2 pending uploads |
| PUT | `/clips/:id/parts/:n` | owner | Raw part bytes. `Content-Length` required and exact (`part_size`, last part is the remainder). 200 `{ part_number, etag }` |
| POST | `/clips/:id/complete` | owner | `{ parts: [ { part_number, etag } ] }`. Idempotent. 201 `{ id, url, expires_at }` |
| DELETE | `/clips/:id` | owner | Aborts a pending upload, deletes the R2 object, the transcript and the row |
| GET | `/clips/mine` | owner | Own clips, including `status`, `narrated`, `has_transcript` |
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

The scheduled handler runs hourly (`0 * * * *`). In order it purges expired clips
and expired pending uploads (aborting the upload and deleting the R2 object), then
expired sessions, then `login_requests` rows that expired over an hour ago (with
their email addresses, used or not), then `transcribe_usage` rows older than 7
days, then `email_send_usage` rows older than 7 days.

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
(best effort, at most 20), then every clip object is deleted in chunks of 1000.
R2 failures are logged; the rows are the source of truth. Then one D1 batch
deletes the transcripts, clips, every session (web AND desktop), identities,
`transcribe_usage` rows, `login_requests` for the email, and the user. The log
line carries the user id and clip count, never the email. The desktop app is
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
