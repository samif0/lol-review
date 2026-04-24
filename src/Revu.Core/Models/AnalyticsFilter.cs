#nullable enable

namespace Revu.Core.Models;

/// <summary>
/// User-configurable filter applied to Analytics aggregations.
///
/// Any filter left at its default value (null / empty collection) is treated
/// as "no constraint on this dimension". The <see cref="MatchMode"/> controls
/// whether a game must satisfy ALL active dimensions (AND) or ANY active
/// dimension (OR) — within a single multi-select dimension, entries are
/// always OR'd together (a game can't be two champions at once).
/// </summary>
public sealed record AnalyticsFilter
{
    /// <summary>Champions to include. Empty = no champion constraint.</summary>
    public IReadOnlyList<string> Champions { get; init; } = [];

    /// <summary>Roles to include (TOP|JUNGLE|MIDDLE|BOTTOM|UTILITY). Empty = no role constraint.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>Win/loss filter. null = both.</summary>
    public bool? Win { get; init; }

    /// <summary>Mental-rating buckets to include. Empty = no mental constraint.</summary>
    public IReadOnlyList<MentalBucket> MentalBuckets { get; init; } = [];

    /// <summary>Date range preset. null = all time.</summary>
    public DateRangePreset DateRange { get; init; } = DateRangePreset.All;

    /// <summary>Days of week to include (0=Sun…6=Sat). Empty = no day constraint.</summary>
    public IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; } = [];

    /// <summary>Filter on whether currently-active objectives were practiced in the game.</summary>
    public ObjectivePracticeFilter ObjectivePractice { get; init; } = ObjectivePracticeFilter.Any;

    /// <summary>AND = game must match every active dimension. OR = game matches if ANY active dimension matches.</summary>
    public FilterMatchMode MatchMode { get; init; } = FilterMatchMode.All;

    public static AnalyticsFilter None { get; } = new();

    /// <summary>True when every dimension is at its no-op default — caller can skip the filtered path.</summary>
    public bool IsEmpty =>
        Champions.Count == 0 &&
        Roles.Count == 0 &&
        Win is null &&
        MentalBuckets.Count == 0 &&
        DateRange == DateRangePreset.All &&
        DaysOfWeek.Count == 0 &&
        ObjectivePractice == ObjectivePracticeFilter.Any;
}

/// <summary>Mental rating bucket used by the filter + the existing profile charts.</summary>
public enum MentalBucket
{
    Low = 0,   // 1-3
    Mid = 1,   // 4-6
    High = 2,  // 7-10
}

/// <summary>Date range presets for the filter bar.</summary>
public enum DateRangePreset
{
    All = 0,
    Last7Days = 1,
    Last30Days = 2,
    Last90Days = 3,
    YearToDate = 4,
}

/// <summary>
/// Objective-practice filter based on the CURRENTLY active objectives (snapshot).
/// </summary>
public enum ObjectivePracticeFilter
{
    /// <summary>No constraint — all games.</summary>
    Any = 0,
    /// <summary>Only games where every currently-active objective was practiced.</summary>
    AllPracticed = 1,
    /// <summary>Only games where none of the currently-active objectives were practiced.</summary>
    NonePracticed = 2,
    /// <summary>Only games where some were practiced and some weren't.</summary>
    Mixed = 3,
}

public enum FilterMatchMode
{
    All = 0,  // AND
    Any = 1,  // OR
}
