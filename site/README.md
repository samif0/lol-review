# revu.lol site

Static marketing, download, web review and clip-share site for Revu. Served
from Cloudflare Pages, bound to the `revu.lol` apex + `www.revu.lol` redirect.

No build step. Everything in this directory ships as-is.

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

- `index.html` + `app.js`: home page and the in-browser web review tool
- `learn.html` + `download.js`: desktop app page; `download.js` rewrites the
  download button href from the latest GitHub release on page load and falls
  back to the static installer URL
- `clip.html`: shared clip watch page (player, captions, transcript); all of
  its JS lives in the first inline `<script>` so `tests/` can run it
- `privacy.html`, `terms.html`, `discord.html` (`discord/index.html` is an
  identical copy for the `/discord/` route)
- `lightbox.js`: screenshot lightbox on the desktop app page
- `_headers`: security headers and CSPs (the clip page has its own)
- `revu.svg`, `favicon.ico`, `icon-*.png`, `revu-mark.png`: brand assets
  copied from `desktop/electron/branding/`
- `shot-*.png`: desktop app screenshots captured from `desktop/ui` sample data

### CSS

Every page loads the stylesheets in this order:

1. `tokens.css`: design tokens
2. `styles.css`: base, header, footer, buttons, forms, cards, lightbox, legal
   pages and the desktop app page
3. the page stylesheet, if any: `webapp.css` (web review tool on the home
   page) or `clip.css` (clip page)

## Edit + preview locally

```sh
cd site
python -m http.server 8000
# open http://localhost:8000
```

The clip page accepts `?proxy=http://localhost:8787` (localhost or 127.0.0.1
only) to read clips from a local `wrangler dev` proxy.

## Tests

```sh
node --test site/tests
```

## Deploy

Commit first, then deploy manually via wrangler (must `cd site` first):

```sh
cd site
npx wrangler pages deploy . --project-name=revu-site --branch=main
```

Deploys are not triggered by git. Deploying an uncommitted or stale tree has
clobbered the live site before, so always deploy from a committed checkout.

## Do not touch

- `proxy/` (Cloudflare Worker at `revu-proxy.lol-review.workers.dev` and
  `clips.revu.lol`): serves `/auth/*`, the Riot proxy and clip sharing. Keep
  separate.
- `revu.lol` MX records: the Resend sender `login@revu.lol` depends on
  them. Pages custom-domain setup only touches A/CNAME at apex + www.
