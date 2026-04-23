# revu.lol landing site — handoff plan

Goal: build and deploy a single-page marketing/download site at `revu.lol`
so users don't have to navigate GitHub Releases to install Revu. Host on
Cloudflare Pages (the domain already lives on Cloudflare; auth emails go
out from `login@revu.lol` via Resend).

Status: **not yet executed.** Starts from a clean slate — no `site/` or
`web/` directory exists in the repo yet.

Time estimate: **~2 hours** end-to-end including DNS wiring and a first
deploy, assuming no surprises with Cloudflare account access.

---

## Scope

### In scope for v1

- Single-page static site. One `index.html`, one stylesheet, no JS build
  step. Designed to match the Revu HUD aesthetic (dark + violet/gold).
- Elevator pitch + a screenshot (or two) of the Dashboard.
- Download button that deep-links to the latest `Setup.exe` Velopack
  asset on GitHub Releases.
- Tiny runtime JS that fetches the latest release tag from the GitHub
  API and rewrites the download-button href. Falls back to the static
  GitHub Releases page if the API call fails.
- Hosted on Cloudflare Pages, bound to `revu.lol` apex + `www.revu.lol`
  redirect.
- HTTPS via Cloudflare (automatic).

### Explicitly out of scope for v1

- Blog, changelog, docs. Add later with a proper SSG if we want those.
- Auth / login page. `/auth/*` stays on the Worker at
  `revu-proxy.lol-review.workers.dev`.
- Installation instructions beyond "download and run." Velopack handles
  the rest.
- Newsletter signup, analytics, A/B testing. Ship without.
- Mobile-specific layout beyond basic responsive flex.

### Deliberately NOT touching

- The Cloudflare **Worker** (`proxy/`). That keeps serving `/auth/*` and
  Riot API proxying. Do NOT co-host the landing page there; keep them
  as separate Cloudflare resources so the Worker's rate limits and
  secret surface stay narrow.
- `revu-proxy.lol-review.workers.dev` — the Worker's URL. Stays.
- `login@revu.lol` Resend sender. The MX records on `revu.lol` must
  keep routing to Resend. Cloudflare Pages takes over HTTP(S) but DOES
  NOT touch MX. Verify this after DNS changes.

---

## Repo layout

Create at the repo root, not inside `src/`:

```
site/
  index.html
  styles.css
  download.js          # rewrites download-button href from GH API
  public/
    favicon.ico        # reuse src/Revu.App/Assets/revu.ico rescaled
    og-image.png       # 1200x630 social card
    screenshot-dashboard.png
  README.md            # how to edit + deploy
  wrangler.toml        # Cloudflare Pages project config
```

Why `site/` not `web/` or `www/`: avoids confusion with the existing
`.wrangler/` build cache and `proxy/` Worker. Clearly labelled.

---

## Design direction

Match the Revu app HUD exactly. Colors + fonts pulled directly from
`src/Revu.App/Themes/AppTheme.xaml`:

### Palette (from AppTheme.xaml)

```css
:root {
  --bg-canvas: #050409;       /* ShellCanvasBrush */
  --bg-sidebar: #0C0B12;      /* SidebarBackgroundBrush */
  --bg-card: #14121E;         /* CardBackgroundBrush */
  --bg-card-alt: #18162A;     /* CardAltBackgroundBrush */
  --bg-input: #110F1A;        /* InputBackgroundBrush */

  --fg-primary: #F0EEF8;      /* PrimaryTextBrush */
  --fg-secondary: #7A6E96;    /* SecondaryTextBrush */
  --fg-muted: #4A3E60;        /* MutedTextBrush */

  --accent-violet: #A78BFA;   /* AccentBlueBrush / AccentPurpleBrush */
  --accent-violet-dim: #1A1430;
  --accent-gold: #C9956A;     /* AccentGoldBrush — priority / focus */
  --accent-gold-dim: #261C12;
  --accent-teal: #8A7AF2;
  --win-green: #7EC9A0;
  --loss-red: #D38C90;

  --border-subtle: #1F1A30;
}
```

### Fonts (self-host; mirror the app)

```
/public/fonts/
  Orbitron.woff2              # DisplayFont  — headline
  Rajdhani-Bold.woff2         # HeadingBoldFont
  ShareTechMono-Regular.woff2 # MonoFont — caption/meta
  Exo2.woff2                  # BodyFont — prose
```

Source TTFs live at `src/Revu.App/Assets/Fonts/`. Convert to woff2 for
the web (smaller + better caching). Use a one-off script or an online
converter; check them in under `site/public/fonts/`.

`@font-face` declarations go in `styles.css`. Licensing: Orbitron,
Rajdhani, Share Tech Mono, Exo 2 are all Google/SIL OFL — redistributable
as self-hosted webfonts. Keep a `LICENSE` or attribution comment in
styles.css.

### Visual conventions

Draw from the app's existing HUD language:

- **Corner brackets** around the primary card — echoes the in-app
  `CornerBracketedCard`. Pure CSS (absolutely-positioned pseudo-
  elements with bordered corners).
- **Monospace eyebrow** labels in Share Tech Mono, uppercase, wide
  letter-spacing (`letter-spacing: 0.25em`). Matches the in-app
  `SectionTitle` / eyebrow style.
- **Wide-letter-spaced violet headline** for "Revu" using Orbitron,
  like the sidebar wordmark in the app.
- **Blinking cursor** next to the wordmark — copy the `.hero-cursor`
  effect from `mockups/app-mockup.html` (there's a `HeroHeader` control
  with a `CursorTextBlock` — port to CSS `@keyframes blink`).
- **Bronze-gold accent** (`--accent-gold`) reserved for the primary
  download button — matches the app's "priority objective" accent so
  users who later install see the same visual cue.
- **Thin violet border** around cards (`1px solid var(--accent-violet)`
  at ~30% opacity), matching `CornerBracketedCard` in the app.

### Layout sketch

Single column, max-width ~720px, centered:

```
┌─────────────────────────────────────┐
│      [ corner ] [ corner ]          │
│                                     │
│        — WELCOME                    │
│                                     │
│          REVU ▮                     │
│    RANKED LEAGUE, REVIEWED          │
│                                     │
│  Revu is a ranked-only League       │
│  companion that turns every game    │
│  into data you can learn from.      │
│                                     │
│    [ DOWNLOAD FOR WINDOWS ]         │
│          v2.12.0 · .exe             │
│                                     │
│   [ screenshot of Dashboard ]       │
│                                     │
│  • Auto-captures every ranked match │
│  • Structured review in seconds     │
│  • Tracks objectives + rules        │
│  • Local-first — your data stays    │
│    on your machine                  │
│                                     │
│      [ corner ] [ corner ]          │
└─────────────────────────────────────┘
          github · v2.12.0
```

---

## Download button behavior

The button's static `href` points at the GitHub Releases page (always
works). On page load, `download.js` fetches the latest release asset URL
and swaps the href + text label:

```js
// download.js
(async () => {
  const btn = document.querySelector('#download-btn');
  if (!btn) return;
  try {
    const res = await fetch(
      'https://api.github.com/repos/samif0/lol-review/releases/latest'
    );
    if (!res.ok) return;  // fall back to static href
    const data = await res.json();
    // Velopack publishes as Revu-<ver>-win-Setup.exe or similar.
    // Match any Setup.exe asset.
    const asset = data.assets.find((a) => /setup\.exe$/i.test(a.name));
    if (!asset) return;
    btn.href = asset.browser_download_url;
    const versionLabel = document.querySelector('#download-version');
    if (versionLabel) versionLabel.textContent = data.tag_name;
  } catch {
    // Network failed; static href still works.
  }
})();
```

**Fallback href** (set in the HTML directly so download works even with
JS disabled):
```html
<a id="download-btn"
   href="https://github.com/samif0/lol-review/releases/latest">
  Download for Windows
</a>
```

---

## Cloudflare Pages deployment

### One-time setup

1. **Verify revu.lol is on Cloudflare.** Check in the CF dashboard that
   `revu.lol` appears under your zones. If it isn't, the user needs to
   add it there first and change nameservers at the registrar. Without
   this the Pages custom-domain binding won't work.

2. **Confirm MX records for Resend are intact.** In the CF DNS panel
   for `revu.lol`, note the MX records pointing to Resend
   (likely `feedback-smtp.*.amazonses.com` or similar). These must
   survive any DNS edits below.

3. **Install wrangler locally if not already there.** Should be
   installed — we use it for the proxy Worker. Check with
   `wrangler --version`.

### Create the Pages project

```sh
cd site
npx wrangler pages project create revu-site --production-branch=main
```

This gives a preview URL like `revu-site.pages.dev`. Test there before
attaching the custom domain.

### Deploy

```sh
cd site
npx wrangler pages deploy . --project-name=revu-site --branch=main
```

This uploads `site/` wholesale. No build step needed (no framework).
If the repo grows and we add e.g. Astro, swap this for
`npm run build && npx wrangler pages deploy dist`.

### Wire revu.lol to the Pages project

In the Cloudflare dashboard:
1. Pages → revu-site → Custom domains → Set up a custom domain
2. Enter `revu.lol`. CF auto-creates a CNAME flattening to the pages.dev
   hostname. Do NOT manually add the CNAME — the custom-domain wizard
   handles apex flattening via Cloudflare's CNAME-at-root feature.
3. Repeat for `www.revu.lol`. Then add a **page rule or bulk redirect**:
   `www.revu.lol/* → https://revu.lol/$1` (301).
4. TLS: Cloudflare auto-provisions a cert. Wait ~5 min, then
   `curl -I https://revu.lol` should return 200.

### Verify MX is intact

After the DNS changes above:

```sh
dig revu.lol MX +short
# Should still return the Resend MX records unchanged.
```

If MX disappeared, re-add from the DNS notes you took in step 2.
Test by sending a login email from the dev app — you should still
receive the code.

---

## CI / automation (optional — v1.1)

For v1, deploy manually via `wrangler pages deploy`. Later, a GitHub
Actions job on push-to-main can:

```yaml
# .github/workflows/deploy-site.yml
on:
  push:
    branches: [main]
    paths: ['site/**']
jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: cloudflare/wrangler-action@v3
        with:
          apiToken: ${{ secrets.CLOUDFLARE_API_TOKEN }}
          accountId: ${{ secrets.CLOUDFLARE_ACCOUNT_ID }}
          command: pages deploy site --project-name=revu-site
```

Needs two new repo secrets (`CLOUDFLARE_API_TOKEN`,
`CLOUDFLARE_ACCOUNT_ID`). Defer until we're actually editing the site
often.

---

## Copy (draft — edit before shipping)

Open for your wordsmithing. Baseline that matches the app's tone:

- **Eyebrow**: `— WELCOME`
- **Headline**: `Revu`
- **Subhead**: `RANKED LEAGUE, REVIEWED`
- **Pitch** (one short paragraph): "Revu is a ranked-only League
  companion that turns every game into data you can learn from. It
  captures matches automatically, helps you write a quick review in
  seconds, and tracks the habits you're trying to build."
- **Download button**: `DOWNLOAD FOR WINDOWS`
- **Download sub-label**: `v2.12.0 · Windows 10/11 · ~80 MB`
  (version auto-updates via `download.js`; size is static rough estimate)
- **Bullets** (4 max):
  - Auto-captures every ranked match — no manual entry
  - Write a structured review in seconds
  - Track objectives + rules across sessions and seasons
  - Local-first — your games stay on your machine
- **Footer**:
  - Left: `github.com/samif0/lol-review` (link)
  - Right: `v2.12.0` (auto-updated)

---

## Screenshot source

Capture from the running dev build. A clean Dashboard view with one or
two reviewed games + a priority objective visible gives the best first
impression.

1. Launch the dev build: `LOLREVIEW_DIAG_LOGS=1 ./src/Revu.App/bin/Debug/LoLReview.App.exe`
2. Seed a couple of games and an objective (manual-entry if LCU isn't
   available)
3. Capture the window via `Win+Shift+S` or a screenshot tool
4. Crop to just the app window (no Windows chrome), save as
   `site/public/screenshot-dashboard.png`
5. Size: 1400x900 or so. Keep under 400KB — use `pngquant` or
   `tinypng.com` if needed.

Also generate a **social card** at 1200x630 for `og-image.png`. The
hero image inside can be a cropped Dashboard screenshot + the "REVU"
wordmark overlaid.

---

## `<meta>` tags that matter

In `index.html`:

```html
<!-- Identity -->
<title>Revu — ranked League, reviewed</title>
<meta name="description" content="Local-first desktop app that captures every ranked League match and helps you review what happened, with objectives and rules that track across sessions.">

<!-- Open Graph / Twitter -->
<meta property="og:title" content="Revu — ranked League, reviewed">
<meta property="og:description" content="Auto-captures every ranked match. Structured review in seconds. Tracks the habits you're building.">
<meta property="og:image" content="https://revu.lol/og-image.png">
<meta property="og:url" content="https://revu.lol/">
<meta property="og:type" content="website">
<meta name="twitter:card" content="summary_large_image">

<!-- Favicon -->
<link rel="icon" type="image/x-icon" href="/favicon.ico">
```

---

## Gotchas the next session will hit

1. **Cloudflare Pages doesn't like `.wrangler/` at the deploy root.**
   That directory is generated by the proxy Worker's dev builds. If
   the next session runs `wrangler pages deploy .` from the wrong
   directory it'll try to upload the whole repo. Always
   `cd site && wrangler pages deploy .` — never from repo root.

2. **Font files inflate the bundle fast.** Four TTFs at ~100-200 KB
   each = a noticeable TTFB. Convert to woff2 (10-20x smaller) before
   checking in. Alternatively, subset them — most only need Latin
   glyphs. Use `pyftsubset` or `fonttools` if we care.

3. **GitHub API rate limits.** Unauthenticated calls to
   `api.github.com` from a user's browser: 60/hour/IP. For a marketing
   site this is fine (one call per page view, per IP, gets cached).
   But if traffic ever spikes and we hit the limit, the button falls
   back to the static GitHub Releases href — that's why the static
   href is there.

4. **`revu.lol` apex record on Cloudflare.** Apex CNAMEs are only
   possible on Cloudflare via their CNAME-flattening feature (automatic
   when you set up a custom domain in Pages). Don't try to add a raw
   CNAME at `@` via DNS manually — it'll conflict with the Pages
   setup. Let the Pages wizard handle it.

5. **Don't break MX.** Before making any DNS change, screenshot the
   current DNS table in the CF panel. The Pages custom-domain flow
   only touches the apex A/CNAME and the www CNAME. If MX changes
   mysteriously, restore from the screenshot and recheck.

6. **`Setup.exe` asset name may vary.** The Velopack-produced filename
   follows a pattern (current: `LoLReview-win-Setup.exe` because
   packId=LoLReview and that hasn't changed). Verify with:
   ```sh
   curl -s https://api.github.com/repos/samif0/lol-review/releases/latest \
     | python -c "import sys,json;d=json.load(sys.stdin);[print(a['name']) for a in d['assets']]"
   ```
   The `download.js` regex `/setup\.exe$/i` matches any asset ending
   in `Setup.exe` case-insensitively — should be robust to renames.

7. **`lol-review.workers.dev` is NOT being renamed.** That's the
   Cloudflare subdomain the Worker is hosted at. Landing page sits on
   `revu.lol` (the apex domain, separately owned). Two different
   Cloudflare resources. Do not conflate.

8. **JS module vs script.** `download.js` is a plain script for
   simplicity. No `type="module"`. Means `fetch` is fine but no
   imports. If we later need modules, update both the `<script>` tag
   and the file's structure together.

9. **Coach sidecar assets on the release page.** Releases attach
   `coach-core-*.zip` and `coach-ml-*.zip` alongside Setup.exe. The
   regex in `download.js` filters to `Setup.exe` only, so those
   don't pollute the download button. Verify during implementation.

10. **Don't ship this plan file in the deployed site.** Cloudflare
    Pages deploys whatever's at the deploy root. This plan lives in
    `docs/`, not `site/`, so it's safe — but double-check
    `.gitignore`/deploy-root when the session starts.

---

## Ordered step list (for the executing session)

1. **Draft + check in `site/index.html`, `site/styles.css`, `site/download.js`.**
   No deploy yet. Preview locally with any static file server
   (`python -m http.server 8000` inside `site/`).
2. **Generate + check in `site/public/favicon.ico`, `og-image.png`,
   `screenshot-dashboard.png`, and `public/fonts/*.woff2`.**
3. **Convert fonts.** Use `woff2_compress` or `pyftsubset` on the four
   TTFs under `src/Revu.App/Assets/Fonts/`. Check woff2 into `site/public/fonts/`.
4. **Install wrangler if needed**, run `wrangler login` interactively
   (one-time browser flow).
5. **Create the Pages project** — `wrangler pages project create revu-site`.
6. **Deploy to preview** — `wrangler pages deploy site --project-name=revu-site --branch=preview`.
   Visit the pages.dev URL. Verify download button works, font loads,
   og-image preview renders (paste the URL in Discord/Slack to check).
7. **Attach custom domain** `revu.lol` via the CF dashboard. Wait for
   TLS cert (~5 min).
8. **Verify `dig revu.lol MX +short` still returns Resend MX records.**
   Send a login email from the dev app → should still arrive.
9. **Attach `www.revu.lol`** + add the redirect to apex.
10. **Promote to production deploy** —
    `wrangler pages deploy site --project-name=revu-site --branch=main`.
11. **Announce** — link `https://revu.lol` on Discord / social.
12. (Optional) Wire the GitHub Actions workflow from the CI section.

---

## Rollback

- **Bad deploy**: revert to the previous Pages deployment via the CF
  dashboard (Deployments → ... → Rollback). Instant.
- **Broken DNS**: restore from the pre-change screenshot of the CF DNS
  table. Cloudflare DNS changes propagate in ~10s within their edge.
- **Pages project totally broken**: detach the custom domain. `revu.lol`
  goes to Cloudflare's default "not configured" page. Users fall back
  to GitHub Releases directly. No Velopack impact — the app's
  auto-update never touches `revu.lol`.

---

## Out-of-scope follow-ups (post-v1)

- **Blog / changelog** — pick an SSG (Astro is the least-friction
  option given the static-single-file v1) and migrate.
- **Docs site** at `docs.revu.lol` — subdomain off revu.lol, same
  Pages pattern.
- **Changelog auto-populated from GitHub Releases** — tiny build step
  that fetches the release notes and renders them. Avoid unless users
  actually ask for it.
- **Analytics** — if we ever want to measure download click-through,
  Cloudflare's built-in Pages analytics is zero-config and doesn't set
  cookies. No third-party JS.
- **Dark/light mode toggle** — not needed; the HUD aesthetic is
  dark-only by design.
