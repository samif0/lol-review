# Multi-phase objectives + custom prompts + post-game slim-down — handoff plan

Goal: let users design their own learning prompts per objective and
surface them at the right moment (champ select vs. post-game), while
also cutting the post-game review down to what people actually fill in.

Target ship: **v2.15.0.** Starts from `main` at or after the `v2.14.0`
tag (the onboarding + dashboard simplify ship).

Time estimate: **~6-8 hours** end-to-end. Larger than any ship in the
last three weeks — don't try to do it in one sitting without
checkpoints.

---

## Background — what the user asked for

Direct quote from the session that produced this doc:

> I think objectives should have a way to create custom prompts in
> pre/post review. I should be able to create an objective and also
> in that same creation pathway, create prompts that allow me to
> better learn the objective. For example, if I have a 2v2 planning
> objective, I'd want that objective to be marked as a pre-game
> objective and I should be able to create a custom pre-game prompt
> that allows me to input how I think the 2v2 will go, what spells
> are key spells during trading, what the wave state should look like.
> But this prompt should not show up if I don't have the objective
> active or I don't have the objective at all.

Follow-up clarifications in the same conversation:
1. Prompts are free-form text (multi-line). No cap on count per objective.
2. Pre-game surface = the existing Champ Select page (PreGamePage).
3. Active + priority objectives' prompts should render. Priority ones
   get visual distinction (gold accent, "PRIORITY" eyebrow).
4. **An objective can practice any subset of {pre, in, post}** — not
   one-of-three. Schema needs three bool columns replacing the single
   `phase` string.
5. **Post-game review is drastically simplified.** Cut: WentWell,
   Mistakes, FocusNext, OutsideControl, WithinControl, Attribution,
   PersonalContribution, ImprovementNote, non-mental Rating. Keep:
   Mental, Matchup Notes, Objective Practice (with notes + custom
   prompts), and a single "anything else outside objectives" field.
6. Legacy reviews must still render their old fields read-only — user
   has 300+ reviewed games with the old columns populated.

---

## Scope

### In scope for v2.15.0

- **Schema:** add `practice_pregame`, `practice_ingame`, `practice_postgame`
  INT bool columns to `objectives`. Backfill from existing `phase` column.
  Leave `phase` column in place as a fallback (don't drop).
- **Schema:** repurpose `objective_prompts` + `prompt_answers` tables
  (currently shaped for yes/no event prompts; zero consumers in app
  code). Use copy-migrate pattern, not drop — protects any user data
  that might have slipped in.
- **Repo layer:** extend `IObjectivesRepository` (phase bools), rewrite
  `IPromptsRepository` for free-form text (kill the `answer_type`,
  `event_tag`, `event_instance_id`, `event_time_s` fields — not used).
- **Objectives create/edit form:** three phase checkboxes (replacing
  the one-of-three dropdown) + "CUSTOM PROMPTS" section with +/- row
  editor.
- **PreGamePage redesign:** render pre-phase prompts for every active
  objective with `practice_pregame=1`. Priority gets gold accent.
  Persist answers to `prompt_answers` on continue.
- **Post-game review slim-down:** hide cut fields from new reviews,
  keep bindings inert so load path doesn't break. Render custom
  prompts for active objectives with `practice_ingame=1` or
  `practice_postgame=1` (union). Add "General notes" field per
  objective (reuses `game_objectives.execution_note`).
- **Legacy fields card:** new collapsible card in ReviewPage, visible
  only when the loaded game has populated legacy fields. Read-only.
- **Dashboard cleanup:** drop `HasLastFocus` / `LastFocus` wiring —
  the `focus_next` column stops getting written on new reviews, so
  the card would progressively empty out on its own. Clean removal
  is better.
- **Tests:** `PromptsRepositoryTests`, `ObjectivesRepositoryPhaseTests`
  in `Revu.Core.Tests`. Migration test that seeds old `phase` string
  and asserts the three bools get set correctly.

### Explicitly NOT in scope

- Dropping the legacy game columns (`went_well`, `mistakes`, etc.) —
  keep the columns, stop writing to them. Historical data preserved.
- Rewriting the objectives *list* view. Only the create/edit form +
  a small "N prompts" badge on each card.
- History/Archive page changes. Let old reviews continue rendering
  the legacy fields from whatever code path exists there.
- Normalized per-objective notes table. Reuse existing
  `game_objectives.execution_note` for the "general note" field.

### Deliberately leaving undecided

- **Whether prompt count should be soft-capped** (e.g. warning toast
  at 10). User said "no cap, pure freedom." Respect that for v1.
  If UX degrades with 20+ prompts, add a collapse/expand pattern
  later, not a hard cap.
- **Whether completed objectives with answered prompts should surface
  that history anywhere.** For v1, just don't render prompts for
  non-active objectives. Analytics pass can come later.

---

## DB schema changes (in order)

### 1. Add three bool columns to `objectives`

```
ALTER TABLE objectives ADD COLUMN practice_pregame  INTEGER NOT NULL DEFAULT 0;
ALTER TABLE objectives ADD COLUMN practice_ingame   INTEGER NOT NULL DEFAULT 0;
ALTER TABLE objectives ADD COLUMN practice_postgame INTEGER NOT NULL DEFAULT 0;
```

Add to `Schema.MigrateObjectivesPracticePhases` (new array) and append
to `Schema.AllMigrations`.

### 2. Backfill from existing `phase` column

New `BackfillObjectivePracticePhasesAsync` method on `DatabaseInitializer`,
called from `InitializeAsync` after the normalization step:

```sql
UPDATE objectives
SET practice_pregame  = CASE WHEN LOWER(COALESCE(phase, 'ingame')) = 'pregame'  THEN 1 ELSE 0 END,
    practice_ingame   = CASE WHEN LOWER(COALESCE(phase, 'ingame')) = 'ingame'   THEN 1 ELSE 0 END,
    practice_postgame = CASE WHEN LOWER(COALESCE(phase, 'ingame')) = 'postgame' THEN 1 ELSE 0 END
WHERE (practice_pregame = 0 AND practice_ingame = 0 AND practice_postgame = 0);
```

The `WHERE` clause makes this idempotent — once any bool is set, the
backfill is a no-op.

### 3. Repurpose `objective_prompts` and `prompt_answers`

Current schema:
```
objective_prompts(id, objective_id, question_text, event_tag, answer_type, sort_order)
prompt_answers(id, game_id, prompt_id, event_instance_id, event_time_s, answer_value)
```

Desired schema:
```
objective_prompts(id, objective_id, phase, label, sort_order, created_at)
prompt_answers(id, prompt_id, game_id, answer_text, updated_at, UNIQUE(prompt_id, game_id))
```

**Use copy-migrate pattern**, same shape as `NormalizeObjectivesTableAsync`
in [src/Revu.Core/Data/DatabaseInitializer.cs:194](../src/Revu.Core/Data/DatabaseInitializer.cs):
1. Detect legacy shape via PRAGMA table_info.
2. CREATE objective_prompts__migrated + prompt_answers__migrated.
3. Copy rows across with reasonable defaults (phase=`'ingame'`,
   label=old `question_text` if any, answer_text=`''` since the old
   shape was yes/no and we can't meaningfully translate).
4. DROP + RENAME inside a transaction.

**Important:** The pre-migration safety backup already runs before
`InitializeAsync` via `DatabaseSafetyStartupTask`, so a second backup
call here is redundant — don't add one. Just rely on the startup chain.

Update `CreateObjectivePromptsTable` + `CreatePromptAnswersTable`
constants in `Schema.cs` to the new shape so fresh installs get it
right.

### 4. Indexes

Add to the end of `AllCreateStatements`:

```
CREATE INDEX IF NOT EXISTS idx_objective_prompts_objective_id
  ON objective_prompts (objective_id, phase, sort_order);

CREATE INDEX IF NOT EXISTS idx_prompt_answers_game_id
  ON prompt_answers (game_id);
```

---

## Repository layer changes

### `IObjectivesRepository`

Breaking change: the single `string phase` parameter becomes three
bools across `CreateAsync` and `UpdateAsync`. `UpdatePhaseAsync` goes
away — replace with `UpdatePracticePhasesAsync(id, pre, in, post)`.

**`ObjectiveSummary` record** (in
[src/Revu.Core/Data/Repositories/RepositoryReadModels.cs](../src/Revu.Core/Data/Repositories/RepositoryReadModels.cs))
gets `bool PracticePre, bool PracticeIn, bool PracticePost` added.
Keep the old `Phase` field but populate it from whichever bool is set
first (pregame > ingame > postgame) for callers that still read it —
easier than ripping every caller in one patch.

New query:
```csharp
Task<IReadOnlyList<ObjectiveSummary>> GetActiveByPhaseAsync(string phase);
```
`phase` is `"pregame" | "ingame" | "postgame"`. Filters on
`status='active' AND practice_<phase>=1`.

### `IPromptsRepository` — full rewrite

The existing interface is dead code (zero consumers). Safe to reshape.
New shape:

```csharp
public interface IPromptsRepository
{
    Task<long> CreatePromptAsync(long objectiveId, string phase, string label, int sortOrder);
    Task UpdatePromptAsync(long promptId, string phase, string label, int sortOrder);
    Task DeletePromptAsync(long promptId);
    Task<IReadOnlyList<ObjectivePrompt>> GetPromptsForObjectiveAsync(long objectiveId);

    /// Active objectives only, filtered by phase. Ordered priority-first,
    /// then objective created_at, then prompt sort_order.
    Task<IReadOnlyList<ActivePrompt>> GetActivePromptsForPhaseAsync(string phase);

    Task SaveAnswerAsync(long promptId, long gameId, string answerText);
    Task<IReadOnlyList<PromptAnswer>> GetAnswersForGameAsync(long gameId);
}
```

New typed records in `RepositoryReadModels.cs`:
```csharp
public sealed record ObjectivePrompt(long Id, long ObjectiveId, string Phase, string Label, int SortOrder);

public sealed record ActivePrompt(
    long PromptId, long ObjectiveId, string ObjectiveTitle, bool IsPriority,
    string Phase, string Label, int SortOrder);

public sealed record PromptAnswer(
    long PromptId, long ObjectiveId, string ObjectiveTitle, bool IsPriority,
    string Phase, string Label, string AnswerText);
```

### Tests

New file: `src/Revu.Core.Tests/PromptsRepositoryTests.cs`.
- **CRUD roundtrip** (create → read → update → delete single prompt).
- **`GetActivePromptsForPhaseAsync` filters by phase** — seed 3 prompts
  for one objective across 3 phases, query each phase, assert only
  the matching one returns.
- **Status filter** — mark the parent objective completed, assert
  active query returns zero regardless of phase.
- **Practice-bool filter** — create objective with `practice_pregame=1`
  but a prompt with `phase='postgame'`, query `postgame`, assert zero
  results (because the objective doesn't practice postgame).
- **Upsert answer** — call `SaveAnswerAsync` twice for same
  (prompt_id, game_id), assert one row in DB, latest text wins.
- **Priority ordering** — priority objective's prompts sort before
  non-priority in `GetActivePromptsForPhaseAsync`.
- **Cascade delete** — delete an objective, assert all its prompts
  and prompt answers are gone. (Requires FK cascade; if not set up
  by default, add it explicitly to the repo `DeletePromptsForObjective`
  method and call it from `IObjectivesRepository.DeleteAsync`.)

New file: `src/Revu.Core.Tests/ObjectivesRepositoryPhaseTests.cs`.
- **Round-trip bools** — create with `{pre:true, in:true, post:false}`,
  read back, assert bools match.
- **`UpdatePracticePhasesAsync`** — create with one bool, update to a
  different set, read back.
- **Migration backfill** — seed an objective with `phase='pregame'`
  and all three bools=0 (simulating an old row). Run migration.
  Assert `practice_pregame=1`, others 0.
- **Backfill idempotency** — run migration twice, assert nothing
  changes the second time (the `WHERE all_zero` guard does its job).

Extend `TypedRepositoryContractTests` if its contract test asserts
specific `phase` behavior on objectives. Keep the old `phase`
equivalence passing during the transition.

---

## UI layer changes

### ObjectivesPage.xaml + ObjectivesViewModel

**Replace** the existing phase ComboBox (`NewPhaseIndex` 0/1/2) with
three CheckBoxes:
- `PracticePre`, `PracticeIn`, `PracticePost` — all ObservableProperty
  bools.
- "Create" disabled unless at least one is checked (add to
  `OnPracticePre/In/PostChanged` partial methods).

**Add** a "CUSTOM PROMPTS" expander below the phase row (collapsed by
default so existing users don't see new UI until they want it):
- `ObservableCollection<PromptDraftItem> NewPrompts` on the VM.
- `PromptDraftItem` has `Phase` (pre/in/post dropdown), `Label`, and
  sort order. Plus `IsExisting`/`OriginalId` for edit mode so the
  diff-save works.
- Template: `[phase ComboBox] [label TextBox, multi-line auto-wrap] [trash button]`.
- `AddPromptCommand` appends a new row with `Phase = "ingame"` default.
- `RemovePromptCommand` takes a `PromptDraftItem` and removes it from
  the collection.

**On save** (`CreateObjectiveAsync` / update path):
1. Save objective with three bools.
2. For each prompt in `NewPrompts`:
   - If `IsExisting == false`: `CreatePromptAsync`.
   - If `IsExisting == true` and `(Phase, Label, Order)` changed:
     `UpdatePromptAsync`.
3. For each existing prompt ID not in the current `NewPrompts` list:
   `DeletePromptAsync`.

**Existing ObjectiveDisplayItem** gets `PracticePre/In/Post` bools +
a `PromptCount` int. Card template adds a small mono-font eyebrow:
"3 CUSTOM PROMPTS" next to the existing level/score stats.

**Load for edit** — when user clicks "Edit", populate `NewPrompts`
from `GetPromptsForObjectiveAsync(id)` with `IsExisting=true`.

### PreGamePage.xaml + PreGameDialogViewModel

**Add new section** after the matchup-detected card, before matchup
history notes. Structure:

```xml
<controls:CornerBracketedCard>
    <StackPanel Spacing="14">
        <controls:SectionTitle Text="YOUR OBJECTIVES THIS GAME"/>

        <TextBlock ... Visibility="{x:Bind HasNoPreGameObjectives}"
                   Text="No pre-game objectives active. Set one in the Objectives page."/>

        <ItemsRepeater ItemsSource="{x:Bind ActivePreGameObjectives}"
                       Visibility="{x:Bind HasPreGameObjectives}">
            <DataTemplate x:DataType="vm:ObjectivePromptBlock">
                <controls:CornerBracketedCard CardBorderBrush="{x:Bind AccentBrush}">
                    <!-- priority eyebrow, objective title + criteria,
                         one multi-line TextBox per prompt -->
                </controls:CornerBracketedCard>
            </DataTemplate>
        </ItemsRepeater>
    </StackPanel>
</controls:CornerBracketedCard>
```

**VM changes** (in `PreGameDialogViewModel`):
- `ObservableCollection<ObjectivePromptBlock> ActivePreGameObjectives`
  — one block per active objective with `practice_pregame=1`.
- Each block holds: `ObjectiveId`, `ObjectiveTitle`, `IsPriority`,
  `Prompts` (collection of `PromptAnswerDraft` with `PromptId`, `Label`,
  `AnswerText`).
- `AccentBrush` per block: gold when priority, teal otherwise.
- `LoadAsync` calls `_promptsRepo.GetActivePromptsForPhaseAsync("pregame")`
  and groups by `ObjectiveId`.
- `SaveAsync` (wire to whatever "continue" or page-closing event exists):
  persist each `AnswerText` via `SaveAnswerAsync(promptId, gameId, text)`.
  This requires knowing the `gameId` for the upcoming game — check how
  the existing PreGamePage handles identity (may be LCU-derived).

**Decision needed on page-close timing:** answers need to persist *before*
the game starts so they're not lost if the user's PC crashes mid-game.
If there's no clear "continue" event, save on every `AnswerText` change
with a 500ms debounce. Look at how `ReviewDraft` persists drafts for
the pattern.

### ReviewPage.xaml + ReviewViewModel — the big one

**Strategy**: two-phase edit.
1. First, add the new sections and mark the legacy ones with
   `Visibility="Collapsed"` via a new `ShowLegacyFields` property that
   computes on load (= true iff the loaded game has ANY legacy field
   populated).
2. Once the new path is working end-to-end, strip the legacy XAML
   sections into a single collapsible "LEGACY" card that only shows
   when `ShowLegacyFields`.

**Sections to cut** (from [src/Revu.App/Views/ReviewPage.xaml](../src/Revu.App/Views/ReviewPage.xaml)):
- "SELF ASSESSMENT" fields: `WentWell`, `Mistakes`, `FocusNext`
- "Mental Reflection" `ImprovementNote` on low-mental path
- `OutsideControl`, `WithinControl`, `Attribution`, `PersonalContribution`
  (find by grepping the XAML)
- The standalone `Rating` (the 1-5 non-mental one, if it exists
  separately from `MentalRating`)

**Sections to keep + evolve:**
- Hero card (unchanged)
- Game stats strip (unchanged)
- "OBJECTIVE PRACTICE" card — extended: each objective gets a
  "General notes" textbox (maps to `game_objectives.execution_note`,
  already there — just rename the XAML label from "Execution note"
  to "General notes") and, below, any custom prompts with phase
  `ingame` or `postgame` render as labeled multi-line TextBoxes.
- **Mental rating** slider — kept
- **Mental reflection** (if ≤3) — simplify to just "What triggered
  the tilt?" (rename `ImprovementNote` → single-field mental note
  OR just remove entirely and let mental rating carry the signal;
  discuss with owner)
- **Matchup notes** — kept, unchanged
- **"ANYTHING ELSE YOU NOTICED"** — relabel `SpottedProblems` with
  new description: "Anything else worth noting that's NOT one of
  your active objectives?"

**New**:
- `ShowLegacyFields` — bool computed on load from whether the saved
  game has any of WentWell, Mistakes, FocusNext, OutsideControl,
  WithinControl, Attribution, PersonalContribution non-empty.
- "LEGACY FIELDS (READ-ONLY)" card at the bottom, visible when
  `ShowLegacyFields`. Renders the old fields as `TextBlock`s (not
  TextBox — can't edit). Preserves historical review data.

**Save path changes** in `ReviewViewModel.SaveAsync`:
- Continue writing: `MentalRating`, `MatchupNote`, `EnemyLaner`,
  `SpottedProblems`, objective practiced + execution_note.
- Stop writing: all the cut fields. Leave the properties + bindings
  intact so existing draft-load paths don't null-ref.
- Add: for each `ObjectivePromptBlock` in `PostGamePrompts`, call
  `SaveAnswerAsync` per prompt.

### DashboardViewModel cleanup

Remove the `HasLastFocus` / `LastFocus` observable properties and the
`GetLastReviewFocusAsync` call from `LoadAsync`. Delete the "LAST FOCUS"
card from [src/Revu.App/Views/DashboardPage.xaml](../src/Revu.App/Views/DashboardPage.xaml).
Small cleanup — ~20 lines removed.

---

## Ordered step list (for the executing session)

Each step should end with: **build green + unit tests pass**. If you
can't keep that invariant, stop and re-plan.

1. **Schema prep** — add `Schema.MigrateObjectivesPracticePhases`
   migration + new `CreateObjectivePromptsTable` / `CreatePromptAnswersTable`
   constants. Add indexes. Append migration to `AllMigrations`.
2. **DatabaseInitializer extensions** — add
   `BackfillObjectivePracticePhasesAsync` +
   `NormalizeObjectivePromptsTableAsync` (copy-migrate pattern,
   mirroring `NormalizeObjectivesTableAsync`).
3. **Typed models** — add `ObjectivePrompt`, `ActivePrompt`,
   `PromptAnswer` records. Extend `ObjectiveSummary` with three bools.
4. **ObjectivesRepository update** — add bool columns to INSERT/UPDATE,
   new `UpdatePracticePhasesAsync`, new `GetActiveByPhaseAsync`.
   Update `ReadObjective()` to populate bools. Build green.
5. **PromptsRepository rewrite** — full rewrite against new interface.
   Hook cascade-delete from `ObjectivesRepository.DeleteAsync`.
6. **Tests** — add `PromptsRepositoryTests` + `ObjectivesRepositoryPhaseTests`.
   Run. Fix until green.
7. **ObjectivesViewModel** — phase checkbox properties, prompt draft
   collection, +/- commands, diff-save logic. Rebuild.
8. **ObjectivesPage.xaml** — phase checkboxes + prompt editor. Manual
   test: create a new objective with 2 prompts, edit it, delete a
   prompt, verify DB state.
9. **PreGameDialogViewModel + PreGamePage.xaml** — load prompts for
   `practice_pregame=1` active objectives, bind TextBoxes, persist on
   answer change or page close.
10. **ReviewViewModel** — add prompt blocks, general notes, load/save
    wiring. Compute `ShowLegacyFields`. DON'T remove old bindings yet.
11. **ReviewPage.xaml** — add new sections behind visibility flags,
    collapse legacy ones. Verify the page opens and saves correctly
    against an existing game and a new game.
12. **Legacy fields card** — move the hidden sections into one
    collapsible read-only card at the bottom.
13. **DashboardViewModel / DashboardPage.xaml** — remove LastFocus
    wiring + card. Rebuild.
14. **Version bump** — `2.14.0 → 2.15.0` in
    [src/Revu.App/Revu.App.csproj](../src/Revu.App/Revu.App.csproj).
15. **Manual test matrix** — see below.
16. **Commit + tag v2.15.0.** Release workflow takes over.

---

## Manual test matrix

Reset preconditions before running: close app, nothing to blow away
since we're not touching config.

**Objective CRUD with prompts:**
1. Create objective with `{pre, post}` checked, add 2 pre-game prompts
   + 1 post-game prompt. Save.
2. Assert DB: `practice_pregame=1, practice_ingame=0, practice_postgame=1`.
   `objective_prompts` has 3 rows with correct phases.
3. Edit objective, change prompt labels, delete one, add a new one.
   Save. Verify diff applied correctly.
4. Mark objective complete. Go to pre-game / post-game — its prompts
   no longer render.
5. Delete objective. Assert `objective_prompts` and `prompt_answers`
   rows for that objective are gone (cascade or explicit cleanup).

**Pre-game flow:**
1. Active pre-game objective with 2 prompts. Open PreGamePage (or
   trigger via LCU champ select).
2. Assert both prompts render. Priority one has gold eyebrow.
3. Fill in answers. Close page. Relaunch. Verify answers persisted.
4. Change to an objective with 0 prompts. Assert the "YOUR OBJECTIVES
   THIS GAME" section renders with just the objective title + no
   text inputs (or omit the sub-card entirely).
5. No active pre-game objectives. Assert empty state: "No pre-game
   objectives active."

**Post-game flow:**
1. Open review for a NEW game with active post-game objective with
   prompts. Verify Mental + Matchup + Objective Practice (with general
   notes + prompts) + "Anything else" render. Verify NONE of WentWell/
   Mistakes/FocusNext show.
2. Open review for an OLD game (one with `went_well` populated in DB).
   Verify "LEGACY FIELDS" card renders at bottom with the old data
   read-only. New sections above it also render.
3. Save new review. Assert DB: `went_well='', mistakes='',` etc. stay
   empty. `mental_rating`, `matchup_note`, `spotted_problems`,
   `execution_note`, and `prompt_answers.answer_text` are populated.

**Dashboard:**
- Verify "LAST FOCUS" card is gone.

---

## Gotchas

1. **`phase` column isn't dropped.** Old code that reads `phase` still
   works (falls back to pregame/ingame/postgame). New code should read
   the bools. Don't dual-write `phase` on save — it goes stale as
   soon as someone picks two phases, and nothing reads it in new code.
   Downstream cleanup in v2.16 can drop the column once nothing reads it.

2. **`DatabaseSafetyStartupTask` already creates a backup** before
   migrations run. Don't add a second backup call inside
   `NormalizeObjectivePromptsTableAsync`. It's redundant and pollutes
   the log.

3. **Copy-migrate idempotency.** The normalize method should short-
   circuit if the target schema already matches. Check for
   `answer_type` column presence before rewriting — if absent, the
   table is already in the new shape.

4. **Cascade delete on objective deletion.** SQLite foreign keys with
   `ON DELETE CASCADE` only work when `PRAGMA foreign_keys = ON` is
   set per-connection. The existing `ObjectivesRepository.DeleteAsync`
   manually deletes from `game_objectives` — it doesn't rely on FK
   cascade. Mirror that pattern: explicitly
   `DELETE FROM prompt_answers WHERE prompt_id IN (SELECT id FROM objective_prompts WHERE objective_id = @id)`
   then `DELETE FROM objective_prompts WHERE objective_id = @id`
   then the objective itself.

5. **PreGamePage identity of the current game.** Prompts need a
   `game_id` to persist against. Champ select pre-dates the game
   entry in the `games` table — the game row doesn't exist until
   post-game. Options:
   - (a) Store PreGame answers in a separate staging table keyed on
     LCU match ID, migrate to `prompt_answers` when the game row
     appears post-game.
   - (b) Insert a shell `games` row at champ-lock with a placeholder
     `game_id` (whatever LCU provides) and fill it in at post-game.
   - (c) Just persist to a `pre_game_drafts` table keyed on LCU
     session ID with TTL.
   **Recommend (c)** — cleanest, no schema entanglement with the
   mature `games` table, and pre-game notes that don't get matched
   to a game are discardable. Add a `pre_game_draft_prompts` table.
   Requires new migration + test, add ~30 min to the estimate.

6. **Review page is fragile.** ReviewViewModel is 547 lines with
   ~40 observable properties wired into a 631-line XAML. DON'T
   remove ObservableProperties in the VM until after you've
   collapsed the XAML sections — removing the VM property first
   will crash the page at `InitializeComponent`. Do XAML first,
   build, VM second, build.

7. **`review_drafts` table.** There's draft persistence at
   [src/Revu.Core/Data/Schema.cs:327-349](../src/Revu.Core/Data/Schema.cs).
   It serializes review state as the user types. If you cut
   WentWell/Mistakes/FocusNext from the VM, the draft table's
   columns become dead — leave them, same reasoning as the `games`
   table. Future v2.16+ cleanup.

8. **`MentalReflection` on low-mental rating.** Currently shown when
   `MentalRating <= 3`. Its field is `ImprovementNote` — which is on
   the cut list. Decide: either (a) keep a single mental-triggered
   note field (rename `ImprovementNote` → "What triggered this?"
   in the UI, keep the column), or (b) drop the reflection path
   entirely and let mental rating alone carry the signal. User
   feedback in the session suggested cutting, so go with (b) unless
   the owner changes their mind.

9. **Active-prompt sort stability.** Between session reloads, the
   user shouldn't see prompts reorder randomly. Sort key:
   `objective.is_priority DESC, objective.created_at ASC,
    prompt.sort_order ASC, prompt.id ASC`. Use that verbatim in
   `GetActivePromptsForPhaseAsync`.

10. **Dashboard `LastFocus` removal is safe** — nothing else in the
    app consumes `GetLastReviewFocusAsync`. Grep confirms.

---

## Rollback

- **Schema migration breaks things.** Pre-migration safety backup
  is in `%LOCALAPPDATA%\LoLReviewData\backups\safety_backup_*.db`.
  Close app, replace `revu.db` with the backup, reopen.
- **Objective create form breaks.** Revert the VM + XAML changes in
  a single commit — everything downstream (pre-game rendering,
  post-game rendering) is read-only against the DB, so the old
  form coming back doesn't corrupt anything.
- **Pre-game or post-game prompt rendering crashes the page.** Wrap
  the prompt-repo calls in try/catch and render an empty state on
  failure. Logging is enough — the objective practice path still
  works without prompts.
- **Nuclear:** revert the whole branch, ship as v2.14.1 (no user-
  visible changes). The v2.14.0 onboarding work is already in
  production.

---

## Out-of-scope follow-ups

- **Drop the `phase` column on objectives** in v2.16 once confirmed
  no code path reads it.
- **Drop the cut review columns** (`went_well`, `mistakes`, etc.) —
  requires a data-preserving migration or acceptance of loss. Not
  needed for v2.15.
- **Custom-prompt analytics.** "Show me all my 2v2 planning prompt
  answers grouped by opponent champion" — neat, but don't build it
  until the user asks.
- **Prompt templates.** If the user ends up creating similar prompts
  across many objectives, add a "save as template" button. Defer.
- **Active prompts on an in-game overlay.** Out of scope and overlay
  infra doesn't exist yet.

---

## Context for the executing session

Read first:
- This doc.
- [CLAUDE.md](../CLAUDE.md) — owner's preferences, critical rules
  (never overwrite DB, always backup, crashes are severity-1).
- [docs/ONBOARDING_SIMPLIFY_PLAN.md](./ONBOARDING_SIMPLIFY_PLAN.md) —
  prior successful handoff doc, same tone.
- [docs/CODEBASE_ONBOARDING.md](./CODEBASE_ONBOARDING.md) — general
  architecture if unfamiliar.

Relevant code entry points:
- [src/Revu.Core/Data/Schema.cs](../src/Revu.Core/Data/Schema.cs) —
  DDL, migration arrays.
- [src/Revu.Core/Data/DatabaseInitializer.cs](../src/Revu.Core/Data/DatabaseInitializer.cs)
  — `NormalizeObjectivesTableAsync` (line 194) is the pattern to
  mirror for the prompts table rewrite.
- [src/Revu.Core/Data/Repositories/ObjectivesRepository.cs](../src/Revu.Core/Data/Repositories/ObjectivesRepository.cs)
  — full file to extend with bools.
- [src/Revu.Core/Data/Repositories/ObjectivePhases.cs](../src/Revu.Core/Data/Repositories/ObjectivePhases.cs)
  — the phase constants. Keep for backwards-compat reads.
- [src/Revu.Core/Data/Repositories/IPromptsRepository.cs](../src/Revu.Core/Data/Repositories/IPromptsRepository.cs)
  — interface to rewrite.
- [src/Revu.Core/Data/Repositories/PromptsRepository.cs](../src/Revu.Core/Data/Repositories/PromptsRepository.cs)
  — impl to rewrite. Zero app consumers today.
- [src/Revu.Core/Services/BackupService.cs](../src/Revu.Core/Services/BackupService.cs)
  — safety backups already wired at startup via
  `DatabaseSafetyStartupTask`.
- [src/Revu.App/ViewModels/ObjectivesViewModel.cs](../src/Revu.App/ViewModels/ObjectivesViewModel.cs)
  — create/edit form state.
- [src/Revu.App/Views/ObjectivesPage.xaml](../src/Revu.App/Views/ObjectivesPage.xaml)
  — the form XAML + the coach-proposals modal (for style reference).
- [src/Revu.App/ViewModels/PreGameDialogViewModel.cs](../src/Revu.App/ViewModels/PreGameDialogViewModel.cs)
  + [src/Revu.App/Views/PreGamePage.xaml](../src/Revu.App/Views/PreGamePage.xaml)
  — champ select surface.
- [src/Revu.App/ViewModels/ReviewViewModel.cs](../src/Revu.App/ViewModels/ReviewViewModel.cs)
  (547 lines) + [src/Revu.App/Views/ReviewPage.xaml](../src/Revu.App/Views/ReviewPage.xaml)
  (631 lines) — the biggest rewrite surface.
- [src/Revu.App/ViewModels/DashboardViewModel.cs](../src/Revu.App/ViewModels/DashboardViewModel.cs)
  — LastFocus cleanup here.
- [src/Revu.Core.Tests/TypedRepositoryContractTests.cs](../src/Revu.Core.Tests/TypedRepositoryContractTests.cs)
  — existing repo tests, pattern to follow.
- [src/Revu.Core.Tests/TestInfrastructure.cs](../src/Revu.Core.Tests/TestInfrastructure.cs)
  — in-memory SQLite fixtures.
