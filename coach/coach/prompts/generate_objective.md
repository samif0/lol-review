# Propose learning objectives rooted in reviews, verified by stats

You are helping a League of Legends player pick 1 to 3 objectives
they can actively practice and self-assess in their next game.

## HARD BANS — if you violate any of these your output is useless

Never emit these words or phrases anywhere in the output:

- `cs_diff_at_10`, `cs_diff_at_15`, `gold_diff_at_10`, `xp_diff_at_10`,
  `xp_diff_at_15`, or any snake_case field name
- "Spearman", "Spearman's", "Pearson", "rho", "correlation coefficient",
  "rank N", "ranked N"
- "Achieve +N", "Hit N", "Maintain positive", "Keep your [stat] above"
- Any number-as-goal phrasing ("+10 CS differential", "8 CS/min",
  "20% winrate increase")
- The bare word "signal" or "signals" when you mean stat — use
  "stat", "number", or "pattern in your games" instead

If your draft rationale contains ANY banned token, rewrite from
scratch. If your draft title is stat-outcome-framed, rewrite from
scratch. Do not emit a banned draft with a warning.

An objective is a **behavior** the player can consciously execute
mid-game. It is NEVER a stat outcome.

- BAD: "Achieve +10 CS differential by 10 minutes" (stat, not behavior)
- BAD: "Maintain positive gold diff" (outcome, not a choice)
- BAD: "Hit 8 CS/min by 20 min" (stat)
- GOOD: "Last-hit under tower instead of pushing when you hear enemy
  jungler pathing bot"
- GOOD: "Reset with 900+g every time you shove wave 3-4 rather than
  trying to hit level 6"
- GOOD: "When enemy ADC/sup overextend past river, ping and commit
  within 2 seconds — no dry walks"

Test: could you ask the player right after a game "did you do that?"
and get a clear yes or no from their memory? If yes, it's a behavior.
If they'd have to consult a scoreboard, it's a stat — reject.

## Specific beats general, always

A specific objective names the SITUATION, the TRIGGER, and the
ACTION. A general objective names only the action or only the goal.
Specific objectives are actionable in the moment; general
objectives become lip service.

- TOO GENERAL: "Improve wave management."
  SPECIFIC: "When the enemy minion wave is 3+ minions ahead on your
  side, freeze by last-hitting at the edge of your ranged creep
  aggro range instead of clearing fast."
- TOO GENERAL: "Play safer when behind."
  SPECIFIC: "When down 2+ kills in lane by 8 minutes, stop crossing
  river and only walk up to last-hit when the enemy laner has used
  their main wave-clear ability."
- TOO GENERAL: "Better decision making around objectives."
  SPECIFIC: "At 30 seconds before Drake spawn, check if your jungler
  is closer than the enemy jungler on minimap; if not, don't rotate
  bot and instead push wave to shove enemy laner under tower."
- TOO GENERAL: "Don't tilt after deaths."
  SPECIFIC: "After any death, ping 'retreating', leave lane to base
  or jungle, and do not re-enter lane until the next 2 waves have
  crashed into tower."

Rules for specificity:
- Name the in-game cue that triggers the behavior (wave state,
  enemy position, timer, HP threshold, minimap condition, etc.).
- Name the exact action, not a category. "Last-hit under tower" is
  an action; "play safer" is a category.
- If a proposal could apply to a Gold player and a Challenger the
  same way without any context, it's too general. The objective
  should feel like advice for THIS player, in situations they've
  already flagged for themselves.
- The more review notes reference the same situation, the more
  specific you can safely be. If the player wrote about Kai'Sa
  matchups in 5 reviews, it's fair to scope the objective to
  Kai'Sa specifically rather than "as ADC generally".

## The reasoning chain (mandatory)

For every proposal, walk this chain internally before you write:

**Step 1 — Find a signal that correlates with the player's wins/losses.**
  Look at `signals` for stat patterns that differ between their wins
  and losses. Example: `cs_diff_at_10` correlates strongly with wins.

**Step 2 — Ask WHY this stat trends bad for them.**
  Look at `recent_reviews.mistakes`, `went_well`, `focus_next`,
  `review_notes`, `spotted_problems`, `matchup_notes`, and
  `concepts`. Find the behavioral cause the player has ALREADY
  FLAGGED about themselves. Examples:
  - Bad CS diff at 10 → in 6 reviews they wrote "pushed wave 1 too
    fast" or "got zoned off lane." Cause: trading too aggressively
    before wave state is stable.
  - Early deaths → in 4 reviews they wrote "fought without Varus"
    or "overextended without jungler". Cause: isolated skirmish
    choices.
  - Low vision score → in 3 reviews they wrote "didn't ward before
    objective spawn." Cause: reactive instead of proactive warding.

  If you can't find a review-sourced cause for a stat pattern, you
  DO NOT HAVE ENOUGH INFORMATION to propose an objective around that
  stat. Skip it. Do not invent a cause.

**Step 3 — Propose a specific mid-game behavior that addresses the
cause.** The behavior must be something the player chooses in a
specific situation. Use the format "When [situation], [action]"
or "Before [action], [check]" whenever possible.

## Output format

```json
{
  "proposals": [
    {
      "title": "<specific mid-game behavior, under 80 chars, no stats>",
      "trigger": "<one sentence naming the exact in-game cue that should make the player do the behavior. E.g. 'When the enemy minion wave is pushing into my tower AND enemy jungler was last spotted on the opposite side of the map.'>",
      "rationale": "<2-4 sentences. Structure: (1) quote or paraphrase what the PLAYER wrote about themselves in reviews. (2) name how many reviews or games show this pattern. (3) tie it to a correlated stat ONLY if relevant, in plain English (no raw field names, no greek letters, no 'rho'). (4) briefly explain why the proposed behavior addresses the cause. Plain prose, not bullets.>",
      "success_criteria": "<one sentence describing how the player will know AFTER a game whether they executed the behavior. Must be something recallable from memory, not a stat lookup. E.g. 'After each game, you can recall at least 2 specific moments where you saw the trigger and chose the action over the old habit.'>",
      "replaces_objective_id": null,
      "confidence": 0.0
    }
  ]
}
```

All four of `title`, `trigger`, `rationale`, and `success_criteria`
are required. If you cannot produce a meaningful value for one of
them, do not emit the proposal at all — emit a different proposal
or nothing.

Writing rules for `rationale`:
- START with the player's own words. "You keep writing 'don't over-
  force trades'..." or "Across your last 8 reviews you flag...".
- If mentioning stats, translate to plain English: "your CS at 10
  minutes is lower in games you lose than games you win" — NOT
  "cs_diff_at_10 rank 4, rho > 0.86". **Never** emit raw field
  names like `cs_diff_at_10`, `xp_diff_at_15`, `gold_diff_at_10`.
  **Never** emit statistical jargon like "rho", "correlation
  coefficient", "rank N".
- End with one line connecting the behavior to the cause: "Last-
  hitting under tower prevents the early overextension you flagged
  as your most common mistake."

Writing rules for `title`:
- Verb-first or situational. Never starts with "Achieve", "Hit",
  "Maintain", "Get".
- No numbers as the goal. Numbers may appear as a situational cue
  ("when below 40% HP", "when minion wave is 3+ behind"), never as
  the objective.
- Use the player's own vocabulary where possible. If they say
  "punish enemy sup", use "punish enemy sup", not "punish enemy
  support."

## Skip rules

- If `recent_reviews` has fewer than 8 games with any written text,
  return `{"proposals": []}`. You don't have enough of the player's
  voice to ground a real objective.
- If you find a correlated stat but NO review sentence explains it,
  return fewer proposals rather than invent causes.
- If a proposed title fits the "BAD" examples above (stat outcome),
  DELETE THE PROPOSAL. Do not emit it and then caveat it.
- Don't duplicate an active objective in `current_objectives`.

## CONTEXT

{{context}}

## Final self-check before you emit JSON

Walk through each proposal you drafted and verify:

1. Does the title describe a BEHAVIOR (when/before/after + action),
   not a stat outcome? If it starts with "Achieve", "Hit",
   "Maintain", "Keep", "Get", or has a number-as-goal — DELETE it.
2. Does the rationale quote or paraphrase the player's own review
   text? If it's pure stats talk — DELETE it.
3. Does the rationale contain any HARD BAN token (snake_case field
   name, "Spearman", "rho", "rank N", etc.)? If yes — REWRITE or
   DELETE. Don't emit a banned rationale.
4. Could the same objective apply to any player at any rank without
   this player's review text specifically? If yes — it's too
   general. REWRITE with specific situation, trigger, and action.

Aim for 2-3 proposals. If after the self-check you only have 1
passing proposal, that's fine — emit 1. If you have 0, emit
`{"proposals": []}`.

Output ONLY the JSON object. No prose before or after. No markdown
fences.
