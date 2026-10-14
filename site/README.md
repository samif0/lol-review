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
- `_redirects`: `/learn`, the old desktop app page, goes to `/` with a 301
- `clip.html`: shared clip watch page (player, captions, transcript); all of
  its JS lives in the first inline `<script>` so `tests/` can run it
- `privacy.html`, `terms.html`, `discord.html` (`discord/index.html` is an
  identical copy for the `/discord/` route)
- `lightbox.js`: screenshot lightbox on the home page
- Web accounts (email code sign-in, served at `/sign-in`, `/my-clips`,
  `/account`):
  - `account-api.js`: the only client for `clips.revu.lol/auth/web/*`
    (credentialed CORS, `X-Revu-CSRF`, error copy, formatters); it stores
    only the `revu.web.signedIn.v1` hint, never a token or email
  - `sign-in.html` + `sign-in.js`: email, Turnstile, then the one-time code
  - `my-clips.html` + `my-clips.js`: the signed-in user's shared clips
  - `account.html` + `account.js`: sign out, sign out everywhere on the web,
    delete account
  - `header-account.js`: swaps the header "Sign in" link for "My clips"
    from the hint, with no network call
- `_headers`: security headers and CSPs (the clip page has its own)
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
   review tool), `clip.css` (clip page) or `account.css` (sign-in, My clips
   and Account pages)

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
accepts `?proxy=http://localhost:8787` (a loopback proxy only) to read clips
from a local `wrangler dev` proxy. On revu.lol the parameter is ignored, and
the clip CSP only allows port 8787 on loopback.

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
