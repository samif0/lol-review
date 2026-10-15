# revu.lol site

Static marketing, download, web review and clip-share site for Revu. Served
from Cloudflare Pages, bound to the `revu.lol` apex + `www.revu.lol` redirect.

No build step. The deploy uploads the public files in this directory as-is;
`tests/`, this README and `wrangler.toml` are never published.

## Look

The site uses the desktop app's Neutral theme in dark mode: cool greys, Geist
and Geist Mono, hairlines instead of boxes, pill buttons, and one filled
accent for the primary action. On the home page the product screenshots are
the only raised surfaces. Dark only, like the screenshots; no glass, blur,
gradients or uppercase labels.

`tokens.css` copies the `[data-theme="neutral"]` block of
`desktop/ui/appearance.css` value for value, with the same variable names,
then bridges the site's older names (`--text`, `--surface`, `--chrome`, ...)
onto them. It is the only site stylesheet with color literals; every other
stylesheet reads tokens. When the app's Neutral dark palette changes, update
`tokens.css` to match. `tests/visual-system.test.mjs` checks the values, the
literals and the fonts.

Geist and Geist Mono are self-hosted from `fonts/`: byte-identical copies of
`desktop/ui/fonts/geist.woff2` and `geist-mono.woff2` with their SIL Open
Font License texts. `tokens.css` declares them with `font-display: swap`, and
every page preloads `geist.woff2`. The CSP's `font-src 'self'` covers them.

## Files

- `index.html` + `landing.css` + `download.js`: the home page, a
  download-first landing. Every `a[data-download]` links the latest
  `Revu-Setup.exe` statically; `download.js` swaps in the exact asset URL
  from the latest GitHub release, fills the version and size, notes on
  phones and Macs that Revu is a Windows app, and keeps the static links if
  GitHub can't be reached. The header Download pill on every page goes to
  `/#download`
- `review.html` + `app.js`: the in-browser web review tool at `/review`;
  `web-review-lib.js` holds its DOM-free helpers (review notes kept per
  match id, error copy) so `tests/` can run them, and `turnstile-ready.js`
  reserves the Turnstile onload callback before the async loader runs. The
  region list in `review.html` must match the platforms in
  `proxy/src/riot.ts` (a test checks it)
- `clip.html`: shared clip watch page (player, captions, transcript); all of
  its JS lives in the first inline `<script>` so `tests/` can run it
- `vod.html` + `vod.css` and four scripts: shared full game page at
  `/v/<id>` (`_redirects` rewrites it to `/vod`, so every asset path is
  absolute). A player, an expiry banner, a lane of the game's moments
  (game time maps to video time through `gameTimeAtVideoStart`), the
  sharer's notes when they chose to include them, and a Discord report
  contact. It is unlisted: `noindex, nofollow` in the page and in
  `_headers`, and no `og:video` or player card. The scripts load in this
  order:
  - `vod-lib.js` (`RevuVod`): pure helpers and all the page's copy, which
    `tests/` runs directly
  - `vod-player.js` (`RevuVodPlayer`): which player a browser gets, the
    play token (`POST /vod-play/<id>`), the hls.js loader and the engine
  - `vod-live.js` (`RevuVodLive`): the controller for a game shared in
    segments (`format: "hls"`), which can play from 0:00 while the rest
    uploads: waiting, Play when ready, the Play from the start button, the
    line under the player, polling, the rebuffer policy, jumping to a
    moment when it arrives, and every error
  - `vod.js`: the page itself, the shared views, and the MP4 path (a share
    without a format plays `/vod-file/<id>` once its upload is complete)

  Nothing counts a play until the viewer presses play: the MP4 player has
  `preload="none"`, and an HLS game mints its token only on the first play,
  a picked moment, or Play when ready. Which player a browser gets: hls.js
  wherever MediaSource or ManagedMediaSource plays the share (while it
  uploads and after); Safari's own player only for a finished game;
  browsers without MediaSource (iOS before 17.1) wait for the finished game
  and then play it natively; Chromium's own HLS player is never used. The
  tests drive all of it with a fake hls.js (`tests/vod-harness.mjs`)
- `vendor/`: self-hosted third-party scripts, one folder per exact version
  (see Vendored hls.js)
- `privacy.html`, `terms.html`, `discord.html` (`discord/index.html` is an
  identical copy for the `/discord/` route)
- `lightbox.js`: screenshot lightbox on the home page
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
  have their own), and the year-long immutable cache for `/vendor/*`
- `_redirects`: `/learn`, the old desktop app page, goes to `/` with a 301,
  and `/v/:id` serves the game page (`/vod`) with a 200 rewrite
- `revu.svg`, `favicon.ico`, `icon-*.png`, `revu-mark.png`: brand assets
  copied from `desktop/electron/branding/`
- `fonts/`: Geist and Geist Mono (see Look)
- `shot-*.webp` (1600x1000) and `og-image.jpg` (1200x630): Neutral dark
  screenshots of the desktop app's sample data, made by
  `scripts/capture-site-shots.mjs` (see Screenshots). `og-image.jpg` is also
  the image on shared clip cards (`proxy/src/clips.ts`)

### CSS

Every page loads the stylesheets in this order:

1. `tokens.css`: design tokens
2. `styles.css`: base, header, footer, buttons, forms, cards, lightbox and
   legal pages
3. the page stylesheet, if any: `landing.css` (home page), `webapp.css` (web
   review tool), `clip.css` (clip page), `vod.css` (game page) or
   `account.css` (sign-in, My clips and Account pages)

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

The game page's CSP (`/v/*`, `/vod` and `/vod.html`, all three identical)
keeps `script-src 'self'`: `vod-player.js` adds hls.js from `/vendor/` with a
script element and its SRI hash. `media-src` adds `blob:` because hls.js
plays through MediaSource, and `worker-src` is `'none'` because hls.js runs
with `enableWorker: false` (the share is fMP4, so nothing needs
transmuxing). Every block detaches the global CSP first: Pages comma-joins a
header set by several matching rules, and browsers enforce every policy.

## Vendored hls.js

The game page plays shares that are still uploading with hls.js 1.7.3, the
light build, served from `site/vendor/hls-1.7.3/`:

- `hls.light.min.js`: byte-identical to `package/dist/hls.light.min.js` in
  the npm tarball `hls.js-1.7.3.tgz`, whose sha512 is
  `MsPlx6yVW4Qv4C7mEVou4/gk/5cN2dVxOTnrNjPSmyH2f0Ln+/UkG10Ax1PE6OvrmTVPKgQYozTtBaPOuCzq7A==`
- `LICENSE.txt`: `package/LICENSE` from the same tarball. The deploy refuses
  a file with no extension, hence `.txt`

`vod-player.js` loads the file only with `HLS_INTEGRITY`, its sha384 SRI.
While that constant is empty the loader refuses to load anything and the
page says the game couldn't be played (it never falls back to unpinned code
or to Chromium's own HLS player). `_headers` caches `/vendor/*` for a year as
immutable, which is safe only because the version is in the path; the
deploy publishes nothing else from `vendor/`. The root `.gitattributes`
marks `site/vendor/**` `-text`: the deploy copies files as checked out, and a
Windows checkout with `core.autocrlf` would otherwise add a CR to each of the
file's line feeds and break the pin.

Only ever vendor an exact stable release, never a canary, alpha or beta:
malicious hls.js canary builds were published to npm in April 2026
(GHSA-pq9g-f2rr-m4hw). To vendor or update it, in an empty scratch folder
outside the repository (`npm pack` downloads the tarball and runs nothing):

```sh
npm pack hls.js@1.7.3
node -e "console.log(require('crypto').createHash('sha512').update(require('fs').readFileSync('hls.js-1.7.3.tgz')).digest('base64'))"
# must print the sha512 above; stop if it doesn't
tar -xzf hls.js-1.7.3.tgz package/dist/hls.light.min.js package/LICENSE
```

Then copy `package/dist/hls.light.min.js` to
`site/vendor/hls-1.7.3/hls.light.min.js` and `package/LICENSE` to
`site/vendor/hls-1.7.3/LICENSE.txt`, and set `HLS_INTEGRITY` in
`vod-player.js` to the output of:

```sh
node -e "console.log('sha384-' + require('crypto').createHash('sha384').update(require('fs').readFileSync('site/vendor/hls-1.7.3/hls.light.min.js')).digest('base64'))"
```

`tests/vendor.test.mjs` then checks the SRI against the file and the
licence beside it (it skips while the folder is missing). A new version gets
a new folder (`hls-<x.y.z>`), a new `HLS_SRC` and `HLS_INTEGRITY`, updated
paths in `tests/vendor.test.mjs`, and the old folder removed; check its
release notes for changes to the API the page uses (`isSupported`,
`loadSource`, `attachMedia`, `startLoad`, `stopLoad`, `recoverMediaError`,
`destroy`, and the `MANIFEST_PARSED`, `LEVEL_UPDATED` and `ERROR` events),
and play a share that is still uploading in Chrome, Edge, Firefox and
Safari before deploying.

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
parameter is ignored, and both CSPs only allow port 8787 on loopback. Until
hls.js is vendored (see Vendored hls.js), a game shared in segments says
"This video couldn't be played." because the page refuses to load an
unpinned hls.js; MP4 shares play as before.

## Local account dev

Run these in two terminals from the repo root:

```sh
cd proxy && npx wrangler dev            # Worker on 8787, with .dev.vars
cd site && npx wrangler pages dev . --port 8788
```

Open http://localhost:8788 (not 127.0.0.1) in Chrome or Firefox. On
localhost the account pages call `http://localhost:8787` and use the
Turnstile test key.

## Screenshots

The screenshots and `og-image.jpg` are generated, never edited by hand:

```sh
node scripts/capture-site-shots.mjs            # every shot plus og-image.jpg
node scripts/capture-site-shots.mjs --list     # what it captures
```

It starts the desktop Vite dev server on a free port (or uses `--base
<url>`), opens each page of the app shell in headless Edge or Chrome with
`?appearance=neutral-dark`, and writes 1600x1000 `shot-<name>.webp` files
into this folder. The Home shot is the whole 1600x1000 window; each
feature shot is one 800x500 part of a page at twice the pixel density, so
its text stays readable at the size the landing shows it. Outside the desktop host the pages read
`desktop/ui/sample-*.json`, so no database or personal data is involved;
the capture still stops before writing a shot if a Riot name, Riot ID or
email address renders. The clock is fixed, so reruns are reproducible.
Look at every image before committing it.

## Tests

```sh
node --test site/tests
```

The game page's tests run its four scripts in `node:vm` against
`tests/vod-harness.mjs`: the page's DOM, a video element with each
browser's autoplay rule, a fake hls.js that records every call, a fake clock
and a fake proxy. They patch a test value into `HLS_INTEGRITY`, so they run
whether or not hls.js is vendored.

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
