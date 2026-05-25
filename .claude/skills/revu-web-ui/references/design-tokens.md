# Revu Web Design Tokens

This is the cheat sheet for designing Revu web surfaces. Open [site/styles.css](../../../../site/styles.css) for the authoritative tokens; this file explains *roles* and *combinations*.

## Palette

### Surfaces (darkest → brightest)

| Token | Hex | Use |
|---|---|---|
| `--bg-canvas` | `#050409` | Page background under everything |
| `--bg-shell` | `#07060B` | Outer chrome surfaces |
| `--bg-sidebar` | `#0C0B12` | Persistent nav, app shell |
| `--bg-card` | `#14121E` | Standard card / panel |
| `--bg-card-alt` | `#18162A` | Card hover, raised state |
| `--bg-input` | `#110F1A` | Form fields, search boxes |

### Foreground

| Token | Hex | Use |
|---|---|---|
| `--fg-primary` | `#F0EEF8` | Headings, primary body |
| `--fg-secondary` | `#7A6E96` | Secondary body, meta |
| `--fg-muted` | `#4A3E60` | Disabled, placeholder, low-priority |

### Accents

| Token | Hex | Use |
|---|---|---|
| `--accent-violet` | `#A78BFA` | Primary accent — CTAs, corner brackets, eyebrows, focus rings, links |
| `--accent-violet-dim` | `#1A1430` | Violet-tinted dim surface (active tab, selected row) |
| `--accent-gold` | `#C9956A` | Secondary accent — sparingly, for badges or rare emphasis |
| `--accent-gold-dim` | `#261C12` | Gold-tinted dim surface |
| `--accent-teal` | `#8A7AF2` | Tertiary accent (shifted violet) — small data viz only |

### Semantic

| Token | Hex | Use |
|---|---|---|
| `--win-green` | `#7EC9A0` | Win results, success states |
| `--loss-red` | `#D38C90` | Loss results, error states |

### Borders

| Token | Hex | Use |
|---|---|---|
| `--border-subtle` | `#24203A` | Default 1px borders |
| `--border-bright` | `#3D3660` | Hover borders, emphasis |

**Contrast checks (against `--bg-canvas`):** `--fg-primary` ≈ 16.5:1 ✅, `--fg-secondary` ≈ 4.9:1 ✅, `--fg-muted` ≈ 2.6:1 ❌ — only use for non-text (icons, separators) or disabled UI.

## Typography

Four typefaces, four roles. Don't mix them up.

| Token | Family | Role | When |
|---|---|---|---|
| `--font-display` | Orbitron | Wordmark, hero numerals | `REVU` mark, 72px+ display, rare |
| `--font-heading` | Rajdhani Bold | H1/H2 page headings | All-caps headings, tight letter-spacing |
| `--font-mono` | Share Tech Mono | Metadata, IDs, timers, KDA | Match IDs, timecodes, eyebrows |
| `--font-body` | Exo 2 | Body copy, paragraphs | Default — long-form text |

### Scale

| Role | Size | Family | Transform | Letter-spacing |
|---|---|---|---|---|
| Wordmark | 72px / 1 | display | uppercase | 0.18em |
| H1 hero | 40-56px / 1.05 | heading | uppercase | 0.04em |
| H1 page | 28-32px / 1.1 | heading | uppercase | 0.04em |
| H2 | 20-24px / 1.2 | heading | uppercase | 0.04em |
| Eyebrow | 11-12px / 1 | mono | uppercase | 0.25em |
| Body | 16px / 1.55 | body | none | 0 |
| Body small | 14px / 1.5 | body | none | 0 |
| Meta mono | 12-13px / 1.4 | mono | none | 0.04em |

## Spacing

Use only: **4, 8, 12, 16, 24, 32, 48, 64, 96 px.** If you reach for 13 or 27, you're off-rhythm. Card padding usually 32-56px. Section gaps usually 48-64px.

## Motifs

### Corner brackets

Signature pattern. 20×20px violet brackets at the four corners of a card. The recipe lives at [site/styles.css:136-168](../../../../site/styles.css#L136). Use on hero cards, primary CTAs — not on every card.

### Eyebrow label

Mono caps, 11px, `0.25em` letter-spacing, violet, prefixed with a 24×1px violet rule. Sits above an H1. Recipe at [site/styles.css:172-188](../../../../site/styles.css#L172). Every primary section deserves one — they replace the need for breadcrumbs or page titles.

### Scanlines

Body has a fixed `::before` overlay of 1px violet lines every 3px at ~1.2% opacity. Recipe at [site/styles.css:94-107](../../../../site/styles.css#L94). Don't change opacity — it's calibrated to be visible but not nausea-inducing.

### Blinking cursor

The wordmark ends in a 0.4em violet block that blinks every 1.6s. Recipe at [site/styles.css:206-220](../../../../site/styles.css#L206). Only on the wordmark — don't sprinkle blinking cursors elsewhere.

### Glow

Violet glow under elements that deserve it: `box-shadow: 0 0 12px rgba(167, 139, 250, 0.6)`. Use on the cursor, focused interactive elements, and live indicators. Not on cards.

## Interaction states

| State | Treatment |
|---|---|
| Hover (button) | Brighter border (`--border-bright`), text gains glow, background lifts to `--bg-card-alt`. ~150ms ease-out. |
| Active / pressed | Translate Y +1px, drop the glow briefly. |
| Focus visible | 2px violet outline + 2px offset, OR violet inner ring. Never remove without replacement. |
| Disabled | `--fg-muted` text, no border, no hover. |
| Selected (row, tab) | `--accent-violet-dim` background, violet 2px left/bottom border. |

## Wins/losses

`.result.win` → `--win-green` text on a `rgba(126,201,160,0.1)` background. `.result.loss` → `--loss-red` equivalent. Always paired with `WIN` / `LOSS` in caps mono.

## Anti-patterns to refuse

- Light backgrounds.
- Rounded corners > 4px (HUD is angular — rare exceptions for pill-shaped tags).
- Drop shadows on cards (replace with corner brackets + 1px border).
- Gradients that aren't the violet/gold radial backdrop or the standard card linear-gradient.
- Emoji icons (use the wordmark cursor or text symbols if needed; otherwise leave it text-only).
