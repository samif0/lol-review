#nullable enable

using Revu.Core.Models;
using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

public sealed class ReviewDraftRepository : IReviewDraftRepository
{
    private readonly IDbConnectionFactory _factory;

    public ReviewDraftRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<ReviewDraft?> GetAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT game_id, mental_rating, went_well, mistakes, focus_next, review_notes,
                   improvement_note, attribution, mental_handled, spotted_problems,
                   outside_control, within_control, personal_contribution, enemy_laner,
                   matchup_note, selected_tag_ids, objective_assessments, updated_at
            FROM review_drafts
            WHERE game_id = @gameId
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new ReviewDraft
        {
            GameId = reader.GetInt64(0),
            MentalRating = reader.IsDBNull(1) ? 5 : reader.GetInt32(1),
            WentWell = reader.IsDBNull(2) ? "" : reader.GetString(2),
            Mistakes = reader.IsDBNull(3) ? "" : reader.GetString(3),
            FocusNext = reader.IsDBNull(4) ? "" : reader.GetString(4),
            ReviewNotes = reader.IsDBNull(5) ? "" : reader.GetString(5),
            ImprovementNote = reader.IsDBNull(6) ? "" : reader.GetString(6),
            Attribution = reader.IsDBNull(7) ? "" : reader.GetString(7),
            MentalHandled = reader.IsDBNull(8) ? "" : reader.GetString(8),
            SpottedProblems = reader.IsDBNull(9) ? "" : reader.GetString(9),
            OutsideControl = reader.IsDBNull(10) ? "" : reader.GetString(10),
            WithinControl = reader.IsDBNull(11) ? "" : reader.GetString(11),
            PersonalContribution = reader.IsDBNull(12) ? "" : reader.GetString(12),
            EnemyLaner = reader.IsDBNull(13) ? "" : reader.GetString(13),
            MatchupNote = reader.IsDBNull(14) ? "" : reader.GetString(14),
            SelectedTagIdsJson = reader.IsDBNull(15) ? "[]" : reader.GetString(15),
            ObjectiveAssessmentsJson = reader.IsDBNull(16) ? "[]" : reader.GetString(16),
            UpdatedAt = reader.IsDBNull(17) ? 0L : reader.GetInt64(17),
        };
    }

    public async Task UpsertAsync(ReviewDraft draft)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO review_drafts (
                game_id, mental_rating, went_well, mistakes, focus_next, review_notes,
                improvement_note, attribution, mental_handled, spotted_problems,
                outside_control, within_control, personal_contribution, enemy_laner,
                matchup_note, selected_tag_ids, objective_assessments, updated_at
            )
            VALUES (
                @gameId, @mentalRating, @wentWell, @mistakes, @focusNext, @reviewNotes,
                @improvementNote, @attribution, @mentalHandled, @spottedProblems,
                @outsideControl, @withinControl, @personalContribution, @enemyLaner,
                @matchupNote, @selectedTagIds, @objectiveAssessments, @updatedAt
            )
            ON CONFLICT(game_id) DO UPDATE SET
                mental_rating = excluded.mental_rating,
                went_well = excluded.went_well,
                mistakes = excluded.mistakes,
                focus_next = excluded.focus_next,
                review_notes = excluded.review_notes,
                improvement_note = excluded.improvement_note,
                attribution = excluded.attribution,
                mental_handled = excluded.mental_handled,
                spotted_problems = excluded.spotted_problems,
                outside_control = excluded.outside_control,
                within_control = excluded.within_control,
                personal_contribution = excluded.personal_contribution,
                enemy_laner = excluded.enemy_laner,
                matchup_note = excluded.matchup_note,
                selected_tag_ids = excluded.selected_tag_ids,
                objective_assessments = excluded.objective_assessments,
                updated_at = excluded.updated_at
            """;

        cmd.Parameters.AddWithValue("@gameId", draft.GameId);
        cmd.Parameters.AddWithValue("@mentalRating", draft.MentalRating);
        cmd.Parameters.AddWithValue("@wentWell", draft.WentWell);
        cmd.Parameters.AddWithValue("@mistakes", draft.Mistakes);
        cmd.Parameters.AddWithValue("@focusNext", draft.FocusNext);
        cmd.Parameters.AddWithValue("@reviewNotes", draft.ReviewNotes);
        cmd.Parameters.AddWithValue("@improvementNote", draft.ImprovementNote);
        cmd.Parameters.AddWithValue("@attribution", draft.Attribution);
        cmd.Parameters.AddWithValue("@mentalHandled", draft.MentalHandled);
        cmd.Parameters.AddWithValue("@spottedProblems", draft.SpottedProblems);
        cmd.Parameters.AddWithValue("@outsideControl", draft.OutsideControl);
        cmd.Parameters.AddWithValue("@withinControl", draft.WithinControl);
        cmd.Parameters.AddWithValue("@personalContribution", draft.PersonalContribution);
        cmd.Parameters.AddWithValue("@enemyLaner", draft.EnemyLaner);
        cmd.Parameters.AddWithValue("@matchupNote", draft.MatchupNote);
        cmd.Parameters.AddWithValue("@selectedTagIds", draft.SelectedTagIdsJson);
        cmd.Parameters.AddWithValue("@objectiveAssessments", draft.ObjectiveAssessmentsJson);
        cmd.Parameters.AddWithValue("@updatedAt", draft.UpdatedAt > 0 ? draft.UpdatedAt : DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM review_drafts WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        await cmd.ExecuteNonQueryAsync();
    }
}
