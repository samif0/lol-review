#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>
/// v2.18 (F2): game-phase "focus" of an objective, used to match auto-picked
/// clips to it. Laning objectives surface early-game clips; teamfight / mid-late
/// objectives surface skirmishes and objective fights.
///
/// <para>
/// Stored on <c>objectives.focus_phase</c> as one of the constants below.
/// <see cref="Auto"/> (the default / empty) means "infer from the objective's
/// title + skill-area text" via <see cref="Infer"/> — so existing objectives
/// that pre-date this feature, and users who never set the tag, still get
/// correct matching. An explicit tag always wins over inference.
/// </para>
/// </summary>
public static class ObjectiveFocusPhases
{
    /// <summary>No explicit tag — infer the phase from the objective's text.</summary>
    public const string Auto = "";
    public const string Laning = "laning";
    public const string MidLate = "midlate";
    public const string Teamfight = "teamfight";
    public const string Any = "any";
    // v3.x (brief 2026-06-17-15): a "Deaths" focus for objectives about reviewing
    // every death (e.g. objective #27 "Review every single death"), which had no
    // home in the picker and was forced to Any. At the time-window level Deaths
    // behaves like Any (a death can happen any phase); the death-keyed clip match
    // is deferred follow-up work. The tag exists so the objective is legible and
    // the picker offers the right intent.
    public const string Deaths = "deaths";

    public static string Normalize(string? value)
    {
        var compact = (value ?? "")
            .Trim()
            .Replace("-", "", System.StringComparison.Ordinal)
            .Replace("_", "", System.StringComparison.Ordinal)
            .Replace(" ", "", System.StringComparison.Ordinal)
            .ToLowerInvariant();

        return compact switch
        {
            "laning" or "lane" or "early" or "earlygame" => Laning,
            "midlate" or "mid" or "late" or "midgame" or "lategame" => MidLate,
            "teamfight" or "teamfighting" or "fight" or "fighting" => Teamfight,
            "deaths" or "death" or "dying" or "deathreview" => Deaths,
            "any" or "all" => Any,
            _ => Auto,
        };
    }

    /// <summary>ComboBox index: 0 Auto, 1 Laning, 2 Mid/Late, 3 Teamfight, 4 Any,
    /// 5 Deaths. (Deaths is last so existing 0-4 tags keep their index.)</summary>
    public static int ToIndex(string? value) => Normalize(value) switch
    {
        Laning => 1,
        MidLate => 2,
        Teamfight => 3,
        Any => 4,
        Deaths => 5,
        _ => 0,
    };

    public static string FromIndex(int index) => index switch
    {
        1 => Laning,
        2 => MidLate,
        3 => Teamfight,
        4 => Any,
        5 => Deaths,
        _ => Auto,
    };

    public static string ToDisplayLabel(string? value) => Normalize(value) switch
    {
        Laning => "Laning / early game",
        MidLate => "Mid / late game",
        Teamfight => "Teamfighting",
        Deaths => "Deaths",
        Any => "Any phase",
        _ => "Auto (from title)",
    };

    // ── Keyword inference ────────────────────────────────────────────
    //
    // The user wants this to be as accurate as possible on EXISTING objectives
    // (which have no tag), so the word lists are deliberately broad. Matching is
    // case-insensitive, substring-based, over the objective title + skill area.
    // We resolve to the EFFECTIVE phase (never returns Auto): an objective that
    // matches nothing falls back to Any, so it isn't accidentally hidden from
    // every clip.

    // Laning / early-game vocabulary. Includes lane mechanics, wave/CS, the
    // early map, trading, and the lane opponent.
    private static readonly string[] LaningWords =
    [
        "lane", "laning", "early", "early game", "earlygame", "level 1", "level1",
        "level 2", "level 3", "first back", "first recall", "back timing",
        "trade", "trading", "trades", "all-in", "all in", "allin",
        "cs", "creep", "minion", "last hit", "last-hit", "lasthit", "last hitting",
        "farm", "farming", "wave", "waves", "wave management", "wave control",
        "freeze", "freezing", "slow push", "slowpush", "fast push", "crash",
        "bounce", "thin the wave", "stack", "lane state", "push", "pushing",
        "shove", "shoving", "zone", "zoning", "deny", "denying",
        "matchup", "lane opponent", "lane phase", "2v2", "1v1", "1 v 1", "2 v 2",
        "skill matchup", "trading stance", "harass", "poke in lane", "lane poke",
        "ward timing", "lane ward", "river ward", "lane gank", "gank setup",
        "tempo", "lane tempo", "prio", "lane prio", "lane priority",
        "first blood", "firstblood", "early kill", "early death", "cheese",
        "sustain", "manage hp", "health management", "rune", "summoner trade",
        "boots timing", "first item", "early roam", "early recall",
    ];

    // Mid / late-game macro vocabulary. Map movement, objectives, vision control,
    // sidelane/splitpush, waveclear-to-siege, and closing the game.
    private static readonly string[] MidLateWords =
    [
        "mid game", "midgame", "mid-game", "late game", "lategame", "late-game",
        "macro", "rotation", "rotations", "rotate", "roam", "roaming", "roams",
        "map", "map movement", "map awareness", "tempo play", "cross map",
        "objective", "objectives", "dragon", "drake", "drag", "soul", "elder",
        "baron", "nash", "nashor", "herald", "rift herald", "grubs", "void grubs",
        // NOTE: bare "tower"/"turret"/"plate" are intentionally NOT here — "last
        // hit under tower", "tower aggro", "freeze near turret" are all LANING.
        // Only siege/dive contexts are mid/late.
        "tower dive", "tower siege", "dive", "siege",
        "sieging", "inhibitor", "inhib", "nexus", "backdoor",
        "splitpush", "split push", "split", "sidelane", "side lane", "side wave",
        "tp", "teleport", "teleport play", "flank", "pick", "picks", "catch",
        "vision", "ward", "warding", "deep ward", "control ward", "sweep",
        "sweeping", "vision control", "fog", "fog of war", "objective setup",
        "objective control", "neutral", "neutral objective", "soul point",
        "grouping", "group", "grouped", "5v5 setup", "mid-late", "midlate",
        "win condition", "wincon", "close the game", "closing", "end the game",
        "spike", "power spike", "powerspike", "item spike", "tempo window",
    ];

    // Teamfight vocabulary. Combat positioning, engage/disengage, target
    // selection, peeling, and the fight itself.
    private static readonly string[] TeamfightWords =
    [
        "teamfight", "team fight", "teamfighting", "team-fight", "fight", "fights",
        "fighting", "skirmish", "skirmishing", "skirmishes", "5v5", "5 v 5",
        "engage", "engaging", "engages", "disengage", "disengaging", "kiting",
        "kite", "peel", "peeling", "front to back", "frontline", "front line",
        "backline", "back line", "positioning", "position in fight", "spacing",
        "target selection", "focus", "focus fire", "target priority",
        "cooldown tracking", "track cooldowns", "flash track", "ult track",
        "ultimate usage", "ult usage", "combo", "combos", "follow up", "followup",
        "collapse", "collapsing", "wombo", "aoe", "zone control", "fight start",
        "re-engage", "reengage", "second wave", "cleanup", "clean up",
        "dps", "damage output", "stay alive", "survive the fight", "don't int",
        "dont int", "throwing fights", "fight timing", "win the fight",
    ];

    /// <summary>
    /// Resolve the EFFECTIVE focus phase for an objective: if it carries an
    /// explicit tag use that; otherwise infer from its title + skill-area text.
    /// Never returns <see cref="Auto"/> — an untaggable/unmatched objective
    /// resolves to <see cref="Any"/> so it isn't hidden from all clips.
    /// </summary>
    public static string Resolve(string? focusPhase, string? title, string? skillArea)
    {
        var explicitPhase = Normalize(focusPhase);
        if (explicitPhase != Auto)
        {
            return explicitPhase;
        }

        return Infer(title, skillArea);
    }

    /// <summary>
    /// Infer the phase purely from text. Returns <see cref="Laning"/>,
    /// <see cref="MidLate"/>, <see cref="Teamfight"/>, or <see cref="Any"/>
    /// (when nothing matches, or matches are ambiguous across categories).
    /// </summary>
    public static string Infer(string? title, string? skillArea)
    {
        var haystack = $"{title} {skillArea}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return Any;
        }

        var laning = CountMatches(haystack, LaningWords);
        var midLate = CountMatches(haystack, MidLateWords);
        var teamfight = CountMatches(haystack, TeamfightWords);

        // Nothing matched → applies to any phase.
        if (laning == 0 && midLate == 0 && teamfight == 0)
        {
            return Any;
        }

        // Highest signal wins. Teamfight and mid/late are both "later game"; on a
        // tie between those two prefer Teamfight (it's the more specific intent).
        // Laning only wins outright when it has the strict maximum, so a mostly
        // macro/fight objective that happens to mention "lane" once isn't
        // mis-bucketed as laning.
        var max = System.Math.Max(laning, System.Math.Max(midLate, teamfight));
        if (laning == max && laning > midLate && laning > teamfight)
        {
            return Laning;
        }
        if (teamfight == max)
        {
            return Teamfight;
        }
        if (midLate == max)
        {
            return MidLate;
        }
        // Laning tied with a later-game category — treat as the later one.
        return teamfight >= midLate ? Teamfight : MidLate;
    }

    private static int CountMatches(string haystack, string[] words)
    {
        var count = 0;
        foreach (var w in words)
        {
            if (haystack.Contains(w, System.StringComparison.Ordinal))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Does an auto-clip at <paramref name="clipGameTimeS"/> in a game of
    /// <paramref name="gameDurationS"/> match an objective of the given effective
    /// phase? Laning = first <see cref="LanePhaseSeconds"/> (14 min). MidLate /
    /// Teamfight = after lane phase. Any = always. (Teamfight vs mid/late aren't
    /// separable by time alone — both are "post-laning" — so they share the
    /// late-game window; the title category already distinguishes the clips.)
    /// </summary>
    public static bool MatchesClipTime(string effectivePhase, int clipGameTimeS, int gameDurationS)
    {
        return Normalize(effectivePhase) switch
        {
            Laning => clipGameTimeS < LanePhaseSeconds,
            MidLate => clipGameTimeS >= LanePhaseSeconds,
            Teamfight => clipGameTimeS >= LanePhaseSeconds,
            // Any, Auto-resolved-to-Any, and Deaths all match every window (a
            // death can happen in any phase; death-event-keyed matching is
            // deferred follow-up, brief 2026-06-17-15).
            _ => true,
        };
    }

    /// <summary>End of the laning phase, in seconds. Matches TimelineInferenceService.</summary>
    public const int LanePhaseSeconds = 14 * 60;
}
