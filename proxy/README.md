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
