#nullable enable

namespace LoLReview.Core.Data.Repositories;

/// <summary>Result of checking a single rule for violations.</summary>
public sealed record RuleViolation(
    Dictionary<string, object?> Rule,
    bool Violated,
    string Reason);

/// <summary>CRUD + violation checking for user-defined gaming rules.</summary>
public interface IRulesRepository
{
    Task<long> CreateAsync(string name, string description = "", string ruleType = "custom",
        string conditionValue = "");

    Task<IReadOnlyList<Dictionary<string, object?>>> GetAllAsync();

    Task<IReadOnlyList<Dictionary<string, object?>>> GetActiveAsync();

    Task<Dictionary<string, object?>?> GetAsync(long ruleId);

    Task ToggleAsync(long ruleId);

    Task DeleteAsync(long ruleId);

    /// <summary>
    /// Check all active rules and return violation results.
    /// Rule types: no_play_day, no_play_after, loss_streak, max_games, min_mental, custom.
    /// </summary>
    Task<IReadOnlyList<RuleViolation>> CheckViolationsAsync(
        IReadOnlyList<Dictionary<string, object?>>? todaysGames = null,
        int? mentalRating = null);
}
