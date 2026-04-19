#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Services;

public sealed record ProcessGameEndRequest(
    GameStats Stats,
    int MentalRating = 5,
    int PreGameMood = 0,
    IReadOnlyList<long>? PreGamePracticedObjectiveIds = null);

public sealed record ProcessGameEndResult(
    long? GameId,
    bool IsSkipped,
    bool IsRecovered)
{
    public bool WasSaved => GameId is not null;
}

public sealed record MissedGameCandidate(
    long GameId,
    long Timestamp,
    GameStats Stats);

public sealed record ReconcileMissedGamesRequest(
    IReadOnlyList<MissedGameCandidate> SelectedGames,
    IReadOnlyList<long> DismissedGameIds,
    int MentalRating = 5,
    int PreGameMood = 0);

public sealed record ReconcileMissedGamesResult(
    int CandidateCount,
    int SelectedCount,
    int IngestedCount,
    int DismissedCount);
