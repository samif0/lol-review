#nullable enable

using System.Text.Json;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Services;

/// <summary>Keep practice credited by a VOD action in sync with an unfinished review.</summary>
public static class ObjectivePracticePersistence
{
    public static async Task MarkPracticedAsync(
        IObjectivesRepository objectives,
        long gameId,
        long objectiveId,
        IReviewDraftRepository? reviewDrafts = null)
    {
        var existing = await objectives.GetGameObjectivesAsync(gameId);
        var note = existing.FirstOrDefault(row => row.ObjectiveId == objectiveId)?.ExecutionNote ?? "";
        await objectives.RecordGameAsync(gameId, objectiveId, practiced: true, executionNote: note);

        if (reviewDrafts is null || await reviewDrafts.GetAsync(gameId) is not { } draft)
            return;

        List<SaveObjectivePracticeRequest> practices;
        try
        {
            practices = JsonSerializer.Deserialize<List<SaveObjectivePracticeRequest>>(
                draft.ObjectiveAssessmentsJson) ?? [];
        }
        catch (JsonException)
        {
            // Review readers already ignore malformed assessments. Keep the original
            // draft intact; the committed practice above remains authoritative.
            return;
        }

        var index = practices.FindIndex(practice => practice.ObjectiveId == objectiveId);
        if (index >= 0)
            practices[index] = practices[index] with { Practiced = true };
        else
            practices.Add(new SaveObjectivePracticeRequest(objectiveId, true, note));

        // A draft normally overrides committed practice on reload. Advance this one
        // flag too, while retaining draft-only notes and every other assessment.
        draft.ObjectiveAssessmentsJson = JsonSerializer.Serialize(practices);
        draft.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await reviewDrafts.UpsertAsync(draft);
    }
}
