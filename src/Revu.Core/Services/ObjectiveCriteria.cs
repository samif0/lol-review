#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// A machine-checkable objective metric: maps a stable key to a stat extracted
/// from the game's post-game data. <see cref="Extract"/> returns null when the
/// stat isn't available for the game (e.g. laning-at-10 before the timeline
/// backfill has run) — null means "not evaluated", never "failed".
/// </summary>
public sealed record CriteriaMetricDef(
    string Key,
    string Label,
    bool LowerIsBetter,
    Func<GameStats, double?> Extract);

/// <summary>
/// v2.18 (schema v5): executable completion criteria. An objective may declare
/// (criteria_metric, criteria_op, criteria_value), e.g. ("cs_per_min", ">=", 7.0).
/// At post-game the criterion is evaluated against the extracted stats and the
/// pass/fail lands in game_objectives.criteria_met — turning "Maintain 7 CS/min"
/// from decorative text into a measured outcome.
/// </summary>
public static class ObjectiveCriteria
{
    public const string OpAtLeast = ">=";
    public const string OpAtMost = "<=";

    /// <summary>
    /// Supported metrics, in the order the objective form offers them.
    /// Keys are persisted in objectives.criteria_metric — never rename.
    /// </summary>
    public static readonly IReadOnlyList<CriteriaMetricDef> Metrics =
    [
        new("cs_per_min", "CS per minute", LowerIsBetter: false, s => s.CsPerMin),
        new("deaths", "Deaths", LowerIsBetter: true, s => s.Deaths),
        new("vision_score", "Vision score", LowerIsBetter: false, s => s.VisionScore),
        new("wards_placed", "Wards placed", LowerIsBetter: false, s => s.WardsPlaced),
        new("control_wards", "Control wards bought", LowerIsBetter: false, s => s.ControlWardsPurchased),
        new("kda", "KDA", LowerIsBetter: false, s => s.KdaRatio),
        new("kill_participation", "Kill participation (%)", LowerIsBetter: false, s => s.KillParticipation),
        new("cs_total", "Total CS", LowerIsBetter: false, s => s.CsTotal),
        new("cs_at_10", "CS at 10 min", LowerIsBetter: false, s => s.CsAt10),
        new("gold_diff_at_10", "Gold diff at 10 min", LowerIsBetter: false, s => s.GoldDiffAt10),
    ];

    public static CriteriaMetricDef? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (var metric in Metrics)
        {
            if (string.Equals(metric.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return metric;
            }
        }

        return null;
    }

    /// <summary>'&gt;=' and '&lt;=' are the only supported comparators; anything else normalizes to '&gt;='.</summary>
    public static string NormalizeOp(string? op)
        => string.Equals(op?.Trim(), OpAtMost, StringComparison.Ordinal) ? OpAtMost : OpAtLeast;

    /// <summary>
    /// Evaluate a structured criterion against a game's stats.
    /// Returns null when the criterion is unset or the stat is unavailable.
    /// </summary>
    public static bool? Evaluate(string? metricKey, string? op, double threshold, GameStats stats)
    {
        var metric = Find(metricKey);
        if (metric is null)
        {
            return null;
        }

        var actual = metric.Extract(stats);
        if (actual is null)
        {
            return null;
        }

        return NormalizeOp(op) == OpAtMost
            ? actual.Value <= threshold
            : actual.Value >= threshold;
    }

    /// <summary>The measured value for a metric, for "6.8 vs 7.0" display.</summary>
    public static double? Measure(string? metricKey, GameStats stats)
        => Find(metricKey)?.Extract(stats);

    /// <summary>Human-readable criterion line, e.g. "CS per minute ≥ 7". Empty when unset.</summary>
    public static string Describe(string? metricKey, string? op, double threshold)
    {
        var metric = Find(metricKey);
        if (metric is null)
        {
            return "";
        }

        var symbol = NormalizeOp(op) == OpAtMost ? "≤" : "≥";
        return $"{metric.Label} {symbol} {FormatValue(threshold)}";
    }

    /// <summary>Trim trailing zeros so "7.0" renders as "7" but "7.5" stays "7.5".</summary>
    public static string FormatValue(double value)
        => value == Math.Floor(value) ? ((long)value).ToString() : value.ToString("0.#");
}
