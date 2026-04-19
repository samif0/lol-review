"""Post-process LLM output to enforce formatting rules the prompt keeps
missing.

The generate_objective and ask prompts both explicitly forbid:
- Single quotes around phrases ('good mental', 'jungle proximity')
- Literal `[game #N]` references in prose

Gemini keeps doing them anyway. Rather than escalate the prompt
further, this module strips the patterns deterministically after
the model returns. Zero tokens wasted, 100% compliance.
"""

from __future__ import annotations

import re

# Matches single-quoted phrases where content has no apostrophe in the
# middle (so we don't eat contractions like "don't" or "it's"). The
# inner pattern allows 1-60 chars of letters/digits/spaces/hyphens
# between quotes.
#
# Intentional: we DO strip curly single quotes too — those come from
# Google AI sometimes even when asked for plain text.
_QUOTED_PHRASE = re.compile(r"['\u2018\u2019]([A-Za-z][A-Za-z0-9 \-/+]{0,60}[A-Za-z0-9])['\u2018\u2019]")

# Matches "[game #907]" or "game #907" or "games #907 and #908".
# We remove the reference but leave surrounding grammar readable.
_GAME_BRACKET = re.compile(r"\[game\s*#?\d+\]", re.IGNORECASE)
_GAME_INLINE = re.compile(r"\bgame\s+#\d+\b", re.IGNORECASE)
_GAMES_INLINE = re.compile(r"\bgames\s+#\d+(?:\s*(?:,|and|&)\s*#?\d+)*\b", re.IGNORECASE)


def sanitize(text: str) -> str:
    """Remove single-quoted phrases and inline game-number references.

    Safe to call on empty/None-ish input. Always returns a string.
    """
    if not text:
        return text or ""

    # Strip quoted phrases: replace `'jungle proximity'` -> `jungle proximity`
    text = _QUOTED_PHRASE.sub(r"\1", text)

    # Remove bracketed game refs outright: `you wrote [game #907]`
    # becomes `you wrote` (the game number adds no value without a
    # clickable link). We also clean trailing " in " or " (" that was
    # left orphaned.
    text = _GAME_BRACKET.sub("", text)
    text = _GAMES_INLINE.sub("your recent games", text)
    text = _GAME_INLINE.sub("that game", text)

    # Collapse any double-spaces or orphaned punctuation left by the
    # removals.
    text = re.sub(r"  +", " ", text)
    text = re.sub(r" +([,\.])", r"\1", text)
    text = re.sub(r"\(\s*\)", "", text)
    text = re.sub(r"\s+\)", ")", text)
    text = re.sub(r"\(\s+", "(", text)

    return text
