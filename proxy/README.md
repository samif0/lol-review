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
