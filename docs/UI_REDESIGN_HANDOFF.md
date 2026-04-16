# UI Redesign Handoff

**Status:** Phase 1-5 complete, theme foundation shipped to `main` (commit `fd68a6f`). Phases 6+ pending. Visual gap remains between current app and mockup — see "Remaining Gap" below.

**Design reference:** `mockups/app-mockup.html` — the interactive HTML mockup is the source of truth for layout, animations, and effects. Also see `memory/design_spec_v2.md`.

## What Shipped

### Phase 1 — Theme Foundation
- Custom fonts bundled in `src/LoLReview.App/Assets/Fonts/`: Orbitron, Rajdhani (4 weights), Share Tech Mono, Exo 2
- `Themes/AppTheme.xaml` rewritten — violet+bronze palette, 2px border-radius everywhere, font family resources (`DisplayFont`, `HeadingFont`, `HeadingBoldFont`, `MonoFont`, `BodyFont`)
- `Styling/AppSemanticPalette.cs` — all hex constants updated
- `Core/Constants/ColorPalette.cs` — aligned with new palette
- 15+ `.cs` files with hardcoded colors updated (ShellPage, GameCard, MoodSelector, TimelineControl, converters, viewmodels)

**Palette:**
- Primary: `#A78BFA` violet
- Secondary: `#C9956A` bronze
- Win: `#7EC9A0`, Loss: `#D38C90`
- Text: `#F0EEF8` / `#7A6E96` / `#4A3E60`
- Backgrounds: `#07060B` / `#0C0B12` / `#14121E`

### Phase 2 — Sidebar Collapse
- `Views/ShellPage.xaml` — 272px text sidebar → 72px icon rail with tooltips
- Vertical "LR" branding, section dividers, compact LCU status indicator
- `ShellPage.xaml.cs` — `SidebarNavButtonStyle` updated for 44x44 icon buttons

### Phase 3 — Dashboard
- Hero header simplified (eyebrow label + DisplayFont title + session banner)
- Connected stat strip (5-column Grid, no gaps, first/last rounded)
- Two-column body preserved (unreviewed games + objectives/focus)

### Phase 4 — Review + VOD + PostGame
- `ReviewPage.xaml` — added "Watch VOD" button in header, moved objectives up, redesigned as styled cards
- `VodPlayerPage.xaml` — bookmarks panel confirmed on right side, MonoFont for timestamps
- `PostGamePage.xaml` — DisplayFont on champion/KDA/mental rating, 2px corner radius

### Phase 5 — Remaining Pages
- All 17 other pages/controls/dialogs updated — inline `CornerRadius` → 2, font references updated where needed

## Remaining Gap vs Mockup

The theme looks right but the **page structures** are still mostly the old layouts with new colors/fonts sprayed on. The mockup has many features that need real implementation work, not just style tweaks:

### High-impact missing pieces
1. **Stat strip styling** — Dashboard stat strip may not render with truly seamless cells (border merging). Verify on PC.
2. **Corner bracket decorators** — the targeting-reticle hover effect on cards (4 small L-shapes at corners) isn't implemented. Would need `VisualStateManager` states on Border or a wrapping control.
3. **Progress rings** — objectives still use the old `ObjectiveProgressBar` (flat bar). Mockup has SVG-style circular rings with gradient stroke + breathing glow. Need a new `ProgressRing` user control.
4. **Hex pattern on hover** — cards and stat boxes don't show the hex tessellation on hover. Need an `ImageBrush` background toggled via `VisualStateManager`.
5. **Win/loss bar glow** — game row left bar currently a solid color, no pulsing glow. Mockup has `box-shadow` pulse which in WinUI = `DropShadow` via Composition + `ScalarKeyFrameAnimation`.
6. **Data stream on sidebar** — vertical light beam traveling down the right edge of the sidebar. Composition animation on a clipped rectangle.
7. **Ambient glow orbs** — three large drifting radial gradients in the background. Could do with `RadialGradientBrush` + translate animations.
8. **Page entrance transitions** — the translate + fade + scale + blur effect. `ThemeTransition` can do translate/fade, but blur needs Composition `GaussianBlurEffect`.
9. **Button hover light sweep** — left-to-right shimmer. Composition animation on a `LinearGradientBrush` position.
10. **Breathing card glow** — box-shadow pulse on cards with `CardGlowStyle`. Needs `DropShadow` + animation.
11. **Pill selection spark** — the violet glow pulsing around selected pills. `DropShadow` animation.
12. **Cursor glow follower** — `PointerMoved` on root + positioned radial gradient element.
13. **Particle canvas** — deferred to Phase 7 (Win2D).
14. **3D magnetic card tilt** — `Composition.Visual` `RotationAxis` on `PointerMoved`. Deferred to Phase 7.

### Layout things still off
- Dashboard right column "Last Focus" card should have **bronze-tinted border** (done via `border-color` in mockup) — likely need to add inline style in XAML.
- VOD Player right panel needs sticky positioning — in WinUI, need a `ScrollViewer` with a fixed side column outside the scrolling area.
- Review page "Watch VOD" button should have **animated glow** (the `breathe` keyframe) — needs Composition animation.
- Concept tag grid — `WrapPanel` not ItemsRepeater for fluid wrapping.

### Small polish items
- Page eyebrow labels should have the typewriter cursor (`_` blinking). WinUI: `TextBlock` with opacity animation.
- Section dividers (`// SECTION NAME ═══════`) — already set up via `sec-t` style in mockup; check if SectionHeaderStyle has the dot + line suffix.
- Empty state illustrations not in mockup but exist in current code — either remove or restyle.

## Phase 6 — Animations (Pending)

Plan laid out in mockup but not implemented. Complexity order:

1. **Easy wins (CSS keyframes → WinUI):**
   - Sidebar data stream (1D translate animation)
   - Status dot pulse (opacity loop)
   - Progress bar tip dot pulse
   - Win/loss bar pulse (opacity loop)

2. **Medium (Composition API):**
   - Card hover: border color + drop shadow on `PointerEntered`/`Exited`
   - Breathing glow on accent cards (`ScalarKeyFrameAnimation` on shadow)
   - Button hover light sweep (animated `LinearGradientBrush`)
   - Page entrance: `ThemeTransition` with custom composition

3. **Hard (defer or skip):**
   - Cursor glow follower (pointer tracking, perf considerations)
   - Particle canvas (needs Win2D NuGet — interop risk in unpackaged mode)
   - 3D magnetic tilt (Composition RotationAxis)
   - Gaussian blur on page transition (Composition effects)

**Suggested approach:** Build a `Helpers/AnimationHelper.cs` with static methods like `AttachHoverGlow(Border)`, `AttachBreathingShadow(Border)`, etc. Apply via code-behind or attached properties.

## Known Risks / Testing Notes

### Font loading
Unpackaged WinUI 3 has finicky `ms-appx:///` resolution. If all text looks like Segoe UI:
- Check `AppDiagnostics` logs at `%LOCALAPPDATA%\LoLReviewData\` for font load errors
- Verify `Assets\Fonts\*.ttf` are copied to output directory after build
- The `.csproj` has `<Content Include="Assets\Fonts\*.ttf" CopyToOutputDirectory="PreserveNewest" />`
- If still broken, fallback: install fonts system-wide on Windows and reference by family name only

### WinUIThemeStubs.xaml
This is a 244KB file containing hand-extracted WinUI system resource overrides (workaround for unpackaged apps). Many built-in control states (ComboBox, Slider, ToggleButton, CheckBox, RadioButton) still reference old jade colors. Visual inconsistency likely on those controls until audited.

### System controls
`Microsoft.UI.Xaml.Controls.XamlControlsResources` is loaded in `Startup/AppResourcesStartupTask.cs` but often fails in unpackaged mode (try/catch with diagnostic logging). The stubs file is the fallback.

### SettingsPage
Has folder-picker rows with hardcoded TextBox widths. Check readability after font swap.

## Key Files for Next Session

- **Theme:** `src/LoLReview.App/Themes/AppTheme.xaml`
- **Palette:** `src/LoLReview.App/Styling/AppSemanticPalette.cs`
- **Shell:** `src/LoLReview.App/Views/ShellPage.xaml` + `.cs`
- **Dashboard:** `src/LoLReview.App/Views/DashboardPage.xaml`
- **Review:** `src/LoLReview.App/Views/ReviewPage.xaml`
- **VOD Player:** `src/LoLReview.App/Views/VodPlayerPage.xaml`
- **Mockup:** `mockups/app-mockup.html` (design truth — open in browser)
- **Design spec:** `memory/design_spec_v2.md` (if .claude directory synced)

## Suggested Next-Session Prompt

> I'm working on the LoL Review WinUI 3 app. Phase 1-5 of a UI redesign shipped in commit `fd68a6f`. Read `docs/UI_REDESIGN_HANDOFF.md` and `mockups/app-mockup.html` for context. I want to continue with Phase 6 animations, or address the "Remaining Gap vs Mockup" items. Let's start with [X].

## Build & Run

```powershell
# Debug build
dotnet restore src\LoLReview.App\LoLReview.App.csproj -r win-x64
msbuild LoLReview.sln /p:Configuration=Debug /p:Platform=x64 /p:RuntimeIdentifier=win-x64

# Run
.\run.bat
```

## Bug Backlog (user mentioned — not addressed yet)

User mentioned having bugs to fix before alpha. Not yet identified or tracked. Should be raised in next session and triaged against the UI work.
