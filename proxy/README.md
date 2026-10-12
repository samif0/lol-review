# revu-proxy

Cloudflare Worker that proxies Riot API requests for the Revu desktop app.

The desktop app calls this Worker with a bearer token; the Worker forwards to the
Riot API using the operator's permanent `RIOT_API_KEY`. Tokens in `ALLOWED_TOKENS`
gate access (Path A). Session tokens from `/auth/verify` also gate access (Path B,
backed by D1).

## Setup

```sh
cd proxy
npm install
wrangler login   # one-time, opens browser
```

## Secrets

```sh
# Paste your permanent Riot key when prompted
wrangler secret put RIOT_API_KEY

# Comma-separated list of per-user static tokens (Path A).
# Generate with: node -e "console.log(require('crypto').randomBytes(24).toString('hex'))"
wrangler secret put ALLOWED_TOKENS

# Resend API key for sending magic-link emails (Path B).
wrangler secret put RESEND_API_KEY
```

To rotate: re-run the same command with a new value.

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

Rename the Worker by changing `name` in `wrangler.toml` and redeploying; update the
desktop app's proxy URL config accordingly.

## Smoke test

```sh
# health (unauthenticated)
curl https://revu-proxy.<sub>.workers.dev/health

# account lookup (authenticated) — returns { puuid, gameName, tagLine }
curl -H "Authorization: Bearer <your-token>" \
  "https://revu-proxy.<sub>.workers.dev/account?riotId=chapy%23hapy&region=na1"

# recent match ids
curl -H "Authorization: Bearer <your-token>" \
  "https://revu-proxy.<sub>.workers.dev/matches?puuid=<PUUID>&region=na1&count=5&queue=420"

# one match
curl -H "Authorization: Bearer <your-token>" \
  "https://revu-proxy.<sub>.workers.dev/match/NA1_5544880520?region=na1"
```

## Logs

```sh
wrangler tail
```

Each request logs `{ token, path, status, ms }` as JSON — no raw tokens, just an
8-char sha256 prefix.

## Rate limits

- Per token: `PER_TOKEN_RPS` (default 4; lowered to 2 for session tokens in Path B)
- Aggregate (all tokens): `AGGREGATE_RPS` (default 18)

Adjust in `wrangler.toml` `[vars]`. If you upgrade to a higher-tier Riot key,
bump `AGGREGATE_RPS` accordingly (stay ~10% below Riot's ceiling).

## Endpoints

All `/matches`, `/match/:id`, `/account` require `Authorization: Bearer <token>`.

| Method | Path | Params | Notes |
|--------|------|--------|-------|
| GET | `/health` | — | No auth |
| GET | `/account` | `riotId=gameName%23tagLine`, `region=na1` | → Riot account (puuid) |
| GET | `/matches` | `puuid`, `region`, `count`, `queue?` | → array of match ids |
| GET | `/match/:id` | `region` | → full match JSON |
| POST | `/auth/signup` | body: `{ email, inviteCode }` | → sends magic link |
| POST | `/auth/login` | body: `{ email }` | → sends magic link |
| GET | `/auth/verify` | `code=XXXX` | → `{ session_token, expires_at }` |
| POST | `/auth/logout` | auth required | invalidates current session |

`region` is a platform id (`na1`, `euw1`, `kr`, …). The Worker maps it to the right
regional cluster per endpoint.

## Clip sharing and transcripts

Owner routes need `Authorization: Bearer <session token>` from a real account
session (D1). A static operator token gets 403 `login_required`. A missing or
bad token gets 401: `unauthorized` on the 3.14.0 routes (uploads, parts,
complete, transcripts, `/transcribe`); the older routes keep their historical
codes (`missing_bearer`, `empty_token`, `invalid_token`). Errors are JSON
`{ error, message? }`. Content-Type checks compare the media type only, so
`text/plain; charset=utf-8` is fine.

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| POST | `/clips?title&champion&duration&narrated=0\|1` | owner | Legacy single-request upload (desktop uses it up to 95 MiB). 201 `{ id, url, expires_at }` |
| POST | `/clips/uploads?title&champion&duration&size&narrated=0\|1` | owner | Start a multipart upload (`video/mp4` or `video/webm`, empty body). duration 1 to 610 s, size up to 2 GiB. 201 `{ id, part_size, part_count, expires_at }`. 6 per minute per user, at most 2 pending uploads |
| PUT | `/clips/:id/parts/:n` | owner | Raw part bytes. `Content-Length` required and exact (`part_size`, last part is the remainder). 200 `{ part_number, etag }` |
| POST | `/clips/:id/complete` | owner | `{ parts: [ { part_number, etag } ] }`. Idempotent. 201 `{ id, url, expires_at }` |
| DELETE | `/clips/:id` | owner | Aborts a pending upload, deletes the R2 object, the transcript and the row |
| GET | `/clips/mine` | owner | Own clips, including `status`, `narrated`, `has_transcript` |
| POST | `/transcribe?offset_ms&duration_ms&language=en` | owner | `text/plain` base64 of a mono 16 kHz 24 kbps MP3 chunk (at most 1 MiB, `Content-Length` required). Whisper via Workers AI. Charged the larger of `duration_ms` and the length the body implies at 24 kbps; a body far longer than `duration_ms` is 400. 7200 audio s per user per day, 10800 s across all users, 30 requests per minute per user |
| PUT | `/clips/:id/transcript` | owner | Version 1 transcript document (at most 256 KiB, 3000 segments; 8 MiB of transcripts per account across live clips). Zero segments removes it. The VTT is generated on read |
| DELETE | `/clips/:id/transcript` | owner | Idempotent |
| GET | `/clip-meta/:id` | public | Adds `narrated`, `has_transcript`, `transcript_language` |
| GET | `/clip-file/:id` | public | Video bytes, Range supported |
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
expired sessions, then `transcribe_usage` rows older than 7 days.

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

Accepted risks (documented, not fixed in v1):

- Lockout: anyone who knows an email can burn its live web codes with 5 bad
  guesses, or use up the 3-per-15-minutes budget through the desktop
  `/auth/login` (which has no Turnstile). The workaround is to wait 15 minutes
  or sign in from the desktop app.
- Email bombing: Turnstile does not stop it through `/auth/login`; only the
  per-email cap bounds it. Durable per-IP and per-email limits are a follow-up.

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
the desktop stays safe because its SQL is unchanged and the identity upsert is
wrapped in try/catch), then deploy the site:

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
