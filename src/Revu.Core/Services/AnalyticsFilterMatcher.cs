#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Services;

/// <summary>
/// Evaluates whether a given <see cref="GameStats"/> row satisfies an
/// <see cref="AnalyticsFilter"/>. Pure logic — no I/O, no DB — so it can be
/// unit-tested in isolation and the service layer can decide *when* to fetch
/// the extra context it needs (e.g. which currently-active objectives were
/// practiced in each game).
/// </summary>
public static class AnalyticsFilterMatcher
{
    /// <summary>
    /// Returns true iff the game passes the filter.
    /// </summary>
    /// <param name="mentalRating">
    /// The mental rating (1-10) associated with the game's review, or null
    /// if the game hasn't been reviewed yet. Games without a rating fail the
    /// mental-bucket filter because there's nothing to bucket.
    /// </param>
    /// <param name="practicedObjectiveIdsForGame">
    /// The subset of CURRENTLY-active objective ids that were marked practiced
    /// in this particular game. Empty if none, or always empty when the caller
    /// isn't using the objective-practice filter.
    /// </param>
    /// <param name="activeObjectiveIdsSnapshot">
    /// The full set of currently-active objective ids. Needed because the
    /// "AllPracticed" / "NonePracticed" / "Mixed" decisions compare against
    /// the active set (not the game's historical state).
    /// </param>
    public static bool Match(
        GameStats game,
        AnalyticsFilter filter,
        int? mentalRating,
        IReadOnlySet<long> practicedObjectiveIdsForGame,
        IReadOnlySet<long> activeObjectiveIdsSnapshot)
    {
        if (filter.IsEmpty) return true;

        // Each dimension evaluates to true/false. In AND mode, every ACTIVE
        // dimension must be true. In OR mode, at least one active dimension
        // must be true. Dimensions at their no-op default contribute
        // "true" to AND (they don't exclude anything) and "false" to OR
        // (they don't include anything on their own).
        var results = new List<DimResult>(7);

        AddResult(results, IsActive: filter.Champions.Count > 0,
            Passes: filter.Champions.Count == 0
                || filter.Champions.Any(c => string.Equals(c, game.ChampionName, StringComparison.OrdinalIgnoreCase)));

        AddResult(results, IsActive: filter.Roles.Count > 0,
            Passes: filter.Roles.Count == 0
                || filter.Roles.Any(r => string.Equals(r, game.Position, StringComparison.OrdinalIgnoreCase)));

        AddResult(results, IsActive: filter.Win is not null,
            Passes: filter.Win is null || filter.Win == game.Win);

        AddResult(results, IsActive: filter.MentalBuckets.Count > 0,
            Passes: filter.MentalBuckets.Count == 0 || MatchesMental(filter.MentalBuckets, mentalRating));

        AddResult(results, IsActive: filter.DateRange != DateRangePreset.All,
            Passes: MatchesDateRange(filter.DateRange, game));

        AddResult(results, IsActive: filter.DaysOfWeek.Count > 0,
            Passes: filter.DaysOfWeek.Count == 0 || MatchesDayOfWeek(filter.DaysOfWeek, game));

        AddResult(results, IsActive: filter.ObjectivePractice != ObjectivePracticeFilter.Any,
            Passes: MatchesObjectivePractice(
                filter.ObjectivePractice, practicedObjectiveIdsForGame, activeObjectiveIdsSnapshot));

        return filter.MatchMode switch
        {
            FilterMatchMode.All => results.TrueForAll(r => !r.IsActive || r.Passes),
            FilterMatchMode.Any => results.Any(r => r.IsActive && r.Passes),
            _ => true,
        };
    }

    private readonly record struct DimResult(bool IsActive, bool Passes);

    private static void AddResult(List<DimResult> bag, bool IsActive, bool Passes)
        => bag.Add(new DimResult(IsActive, Passes));

    private static bool MatchesMental(IReadOnlyList<MentalBucket> buckets, int? rating)
    {
        // Games with no review fail the mental filter by default — there's
        // nothing to bucket.
        if (rating is null) return false;
        foreach (var b in buckets)
        {
            var hit = b switch
            {
                MentalBucket.Low  => rating is >= 1 and <= 3,
                MentalBucket.Mid  => rating is >= 4 and <= 6,
                MentalBucket.High => rating is >= 7 and <= 10,
                _ => false,
            };
            if (hit) return true;
        }
        return false;
    }

    private static bool MatchesDateRange(DateRangePreset preset, GameStats game)
    {
        if (preset == DateRangePreset.All) return true;
        if (game.Timestamp <= 0) return false;
        var played = DateTimeOffset.FromUnixTimeSeconds(game.Timestamp).LocalDateTime;
        var now = DateTime.Now;
        return preset switch
        {
            DateRangePreset.Last7Days  => played >= now.AddDays(-7),
            DateRangePreset.Last30Days => played >= now.AddDays(-30),
            DateRangePreset.Last90Days => played >= now.AddDays(-90),
            DateRangePreset.YearToDate => played.Year == now.Year,
            _ => true,
        };
    }

    private static bool MatchesDayOfWeek(IReadOnlyList<DayOfWeek> days, GameStats game)
    {
        if (game.Timestamp <= 0) return false;
        var played = DateTimeOffset.FromUnixTimeSeconds(game.Timestamp).LocalDateTime;
        return days.Contains(played.DayOfWeek);
    }

    private static bool MatchesObjectivePractice(
        ObjectivePracticeFilter mode,
        IReadOnlySet<long> practicedForGame,
        IReadOnlySet<long> activeSnapshot)
    {
        if (mode == ObjectivePracticeFilter.Any) return true;
        if (activeSnapshot.Count == 0)
        {
            // No currently-active objectives — nothing to judge against.
            // Treat as neutral match so the filter doesn't wipe the dataset
            // when a user has finished all their objectives.
            return mode == ObjectivePracticeFilter.NonePracticed;
        }

        var practicedCount = 0;
        foreach (var id in activeSnapshot)
        {
            if (practicedForGame.Contains(id)) practicedCount++;
        }

        return mode switch
        {
            ObjectivePracticeFilter.AllPracticed  => practicedCount == activeSnapshot.Count,
            ObjectivePracticeFilter.NonePracticed => practicedCount == 0,
            ObjectivePracticeFilter.Mixed         => practicedCount > 0 && practicedCount < activeSnapshot.Count,
            _ => true,
        };
    }
}
