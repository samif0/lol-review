# revu.lol site

Static marketing, download, web review and clip-share site for Revu. Served
from Cloudflare Pages, bound to the `revu.lol` apex + `www.revu.lol` redirect.

No build step. The deploy uploads the public files in this directory as-is;
`tests/`, this README and `wrangler.toml` are never published.

## Look

The site mirrors the desktop app's flat charcoal system: dark only, system
fonts (Segoe UI on Windows), monochrome, a white pill for the primary action
and outlined radius-12 buttons for everything else. No webfonts, glass, blur,
shadows, gradients, uppercase labels or letter-spacing.

`tokens.css` mirrors the `:root` token block at `desktop/ui/styles.css:3374`
(the "Shared application system" layer) with the same variable names, plus
named literals, radii and layout tokens. When the app's palette changes,
update `tokens.css` to match.

## Files

- `index.html` + `app.js`: home page and the in-browser web review tool;
  `web-review-lib.js` holds its DOM-free helpers (review notes kept per
  match id, error copy) so `tests/` can run them; `turnstile-ready.js`
  reserves the Turnstile onload callback before the async loader runs, and
  `version-chip.js` refreshes the header pill version. The region list in
  `index.html` must match the platforms in `proxy/src/riot.ts` (a test
  checks it)
- `learn.html` + `download.js`: desktop app page; `download.js` rewrites the
  download button href from the latest GitHub release on page load and falls
  back to the static installer URL
- `clip.html`: shared clip watch page (player, captions, transcript); all of
  its JS lives in the first inline `<script>` so `tests/` can run it
- `vod.html` + `vod.js` + `vod.css`: shared full game page at `/v/<id>`
  (`_redirects` rewrites it to `/vod`, so every asset path is absolute).
  Native player, an expiry banner, a lane of the game's moments (game time
  maps to video time through `gameTimeAtVideoStart`), the sharer's notes
  when they chose to include them, and a Discord report contact. It is
  unlisted: `noindex, nofollow` in the page and in `_headers`, and no
  `og:video` or player card. `vod.js` puts its pure helpers on
  `RevuVod` for `tests/`
- `privacy.html`, `terms.html`, `discord.html` (`discord/index.html` is an
  identical copy for the `/discord/` route)
- `lightbox.js`: screenshot lightbox on the desktop app page
- Web accounts (email code sign-in, served at `/sign-in`, `/my-clips`,
  `/account`):
  - `account-api.js`: the only client for `clips.revu.lol/auth/web/*`
    (credentialed CORS, `X-Revu-CSRF`, error copy, formatters); it stores
    only the `revu.web.signedIn.v1` hint, never a token or email
  - `sign-in.html` + `sign-in.js`: email, Turnstile, then the one-time code
  - `my-clips.html` + `my-clips.js`: the signed-in user's shared clips and
    full games; a game card says "Full game" and its delete reads "Stop
    sharing"
  - `account.html` + `account.js`: sign out, sign out everywhere on the web,
    delete account
  - `header-account.js`: swaps the header "Sign in" link for "My clips"
    from the hint, with no network call
- `_headers`: security headers and CSPs (the clip page and the game page
  have their own)
- `_redirects`: `/v/:id` serves the game page
- `revu.svg`, `favicon.ico`, `icon-*.png`, `revu-mark.png`: brand assets
  copied from `desktop/electron/branding/`
- `shot-*.png`: desktop app screenshots captured from `desktop/ui` sample data

### CSS

Every page loads the stylesheets in this order:

1. `tokens.css`: design tokens
2. `styles.css`: base, header, footer, buttons, forms, cards, lightbox, legal
   pages and the desktop app page
3. the page stylesheet, if any: `webapp.css` (web review tool on the home
   page), `clip.css` (clip page), `vod.css` (game page) or `account.css`
   (sign-in, My clips and Account pages)

## Scripts and CSP

Every revu.lol page sits inside the web account trust boundary: any script
on revu.lol can call the credentialed account API on `clips.revu.lol`. So
no page may use inline scripts or inline event handlers, and `_headers`
never allows `'unsafe-inline'` in `script-src`. Put page JS in a `.js`
file loaded with `src`.

The one exception is `clip.html`, whose JS stays in its first inline
`<script>` so `tests/` can run it. Its `/clip` and `/clip.html` CSPs allow
that script by sha256, once for LF and once for CRLF line endings. Any edit
to that script must update both hashes in `_headers`;
`tests/headers.test.mjs` recomputes them and fails until they match.

## Edit + preview locally

```sh
cd site
npx wrangler pages dev . --port 8788
# open http://localhost:8788
```

`wrangler pages dev` applies `_headers`, so CSP errors show up in the
console the same way they would in production. It serves the working tree,
`tests/` and this README included; see Deploy for checking exactly which
files a deploy uploads.

When the page itself is served from localhost or 127.0.0.1, the clip page
and the game page accept `?proxy=http://localhost:8787` (a loopback proxy
only) to read from a local `wrangler dev` proxy, for example
http://localhost:8788/v/Abc1234?proxy=http://localhost:8787. On revu.lol the
parameter is ignored, and both CSPs only allow port 8787 on loopback.

## Local account dev

Run these in two terminals from the repo root:

```sh
cd proxy && npx wrangler dev            # Worker on 8787, with .dev.vars
cd site && npx wrangler pages dev . --port 8788
```

Open http://localhost:8788 (not 127.0.0.1) in Chrome or Firefox. On
localhost the account pages call `http://localhost:8787` and use the
Turnstile test key.

## Tests

```sh
node --test site/tests
```

## Deploy

Merge to `main` first, then deploy from an up-to-date, clean `main`
checkout at the repository root:

```sh
node scripts/deploy-site.mjs
```

The script runs `scripts/guard-deploy.mjs` (clean tree, `HEAD` equals
`origin/main` after a fetch, `node --test site/tests` passes), stages only
the public files into `artifacts/site-deploy` (the `pages_build_output_dir`
in `wrangler.toml`), uploads them with the wrangler version pinned in
`proxy/package-lock.json`, and removes the stage. A new file type in this
directory fails the deploy until it is added to the allowlist in
`scripts/deploy-site.mjs`; every file in `.well-known/` is published.
`node scripts/deploy-site.mjs --stage-only` stages without deploying, so
you can inspect the exact upload in `artifacts/site-deploy`.

For a change that touches web accounts, deploy in this order: apply the
proxy D1 migration, deploy the proxy (`npm run deploy` in `proxy/`, also
guarded), then run `node scripts/deploy-site.mjs` for the site.

Keep Cloudflare Rocket Loader off for revu.lol. It rewrites script tags,
which breaks the strict CSP.

Deploys are not triggered by git. Deploying an uncommitted or stale tree has
clobbered the live site before, which is why the deploy script refuses
anything but a clean checkout of `origin/main`.

## Do not touch

- `proxy/` (Cloudflare Worker at `revu-proxy.lol-review.workers.dev` and
  `clips.revu.lol`): serves `/auth/*`, the Riot proxy and clip sharing. Keep
  separate.
- `revu.lol` MX records: the Resend sender `login@revu.lol` depends on
  them. Pages custom-domain setup only touches A/CNAME at apex + www.
