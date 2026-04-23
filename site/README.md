# revu.lol landing site

Static single-page marketing/download site for Revu. Served from
Cloudflare Pages, bound to the `revu.lol` apex + `www.revu.lol` redirect.

No build step. Everything in this directory ships as-is.

## Files

- `index.html` — the single page
- `styles.css` — palette + fonts mirror `src/Revu.App/Themes/AppTheme.xaml`
- `download.js` — rewrites the download button href from the latest GitHub
  release on page load; falls back to the static Releases URL
- `fonts/` — self-hosted woff2 webfonts (Orbitron, Rajdhani, Share Tech Mono, Exo 2)
- `favicon.ico`, `revu-mark.png` — brand assets mirrored from `src/Revu.App/Assets/`

## Edit + preview locally

```sh
cd site
python -m http.server 8000
# open http://localhost:8000
```

## Deploy

Manual deploy via wrangler (must `cd site` first — gotcha #1 in the
handoff plan):

```sh
cd site
npx wrangler pages deploy . --project-name=revu-site --branch=main
```

## Do not touch

- `proxy/` (Cloudflare Worker at `revu-proxy.lol-review.workers.dev`) —
  serves `/auth/*` and the Riot proxy. Keep separate.
- `revu.lol` MX records — the Resend sender `login@revu.lol` depends on
  them. Pages custom-domain setup only touches A/CNAME at apex + www.
