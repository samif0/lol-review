#nullable enable

namespace Revu.Core.Data.Repositories;

/// <summary>Emotion frequency entry for tilt check stats.</summary>
public sealed record EmotionCount(string Emotion, int Count);

/// <summary>Aggregate tilt check statistics.</summary>
public sealed record TiltCheckStats(
    int Total,
    double AvgBefore,
    double AvgAfter,
    double AvgReduction,
    IReadOnlyList<EmotionCount> TopEmotions);

/// <summary>Stores and queries tilt check exercise results.</summary>
public interface ITiltCheckRepository
{
    /// <summary>Save a completed tilt check exercise. Returns the row id.</summary>
    Task<long> SaveAsync(
        string emotion,
        int intensityBefore,
        int? intensityAfter = null,
        string reframeThought = "",
        string reframeResponse = "",
        string thoughtType = "",
        string cueWord = "",
        string focusIntention = "",
        long? gameId = null,
        string ifThenPlan = "");

    /// <summary>Get recent tilt checks, newest first.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetRecentAsync(int limit = 20);

    /// <summary>Get aggregate tilt check stats.</summary>
    Task<TiltCheckStats> GetStatsAsync();
}
