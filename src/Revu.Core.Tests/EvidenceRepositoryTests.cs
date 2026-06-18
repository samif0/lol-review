using Revu.Core.Data.Repositories;
using Revu.Core.Models;

namespace Revu.Core.Tests;

public sealed class EvidenceRepositoryTests
{
    [Fact]
    public async Task UpsertAsync_CreatesUpdatesLinksAndDismissesEvidence()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", false);
        var objectiveId = await scope.Objectives.CreateAsync("Hold wave before objective", "macro");

        var evidenceId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.TimelineRegion,
            SourceId: null,
            SourceKey: "lost-dragon-900-940",
            StartTimeSeconds: 900,
            EndTimeSeconds: 940,
            Title: "Lost Dragon fight"));

        await scope.Evidence.UpdateObjectiveAsync(evidenceId, objectiveId);
        await scope.Evidence.UpdatePolarityAsync(evidenceId, EvidencePolarities.Bad);
        await scope.Evidence.UpdateStatusAsync(evidenceId, EvidenceStatuses.Evidence);

        var rows = await scope.Evidence.GetForGameAsync(gameId);
        Assert.Single(rows);
        Assert.Equal(objectiveId, rows[0].ObjectiveId);
        Assert.Equal("Hold wave before objective", rows[0].ObjectiveTitle);
        Assert.Equal(EvidencePolarities.Bad, rows[0].Polarity);
        Assert.Equal(EvidenceStatuses.Evidence, rows[0].Status);

        await scope.Evidence.UpdateStatusAsync(evidenceId, EvidenceStatuses.Dismissed);
        Assert.Empty(await scope.Evidence.GetForGameAsync(gameId));
        Assert.Single(await scope.Evidence.GetForGameAsync(gameId, includeDismissed: true));
    }

    [Fact]
    public async Task AttachingClipsToObjective_AddsTwoPointsEach_AndDoesNotDoubleCount()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objectiveId = await scope.Objectives.CreateAsync("Roam after pushing", "macro");

        async Task<int> ScoreAsync()
        {
            var o = await scope.Objectives.GetAsync(objectiveId);
            return o!.Score;
        }

        Assert.Equal(0, await ScoreAsync());

        // Tag an existing evidence item onto the objective → +2.
        var firstId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.TimelineRegion,
            SourceId: null,
            SourceKey: "moment-1",
            StartTimeSeconds: 100,
            EndTimeSeconds: 120,
            Title: "Missed roam"));
        await scope.Evidence.UpdateObjectiveAsync(firstId, objectiveId);
        Assert.Equal(2, await ScoreAsync());

        // Re-tagging the SAME objective on the same item must not stack points.
        await scope.Evidence.UpdateObjectiveAsync(firstId, objectiveId);
        Assert.Equal(2, await ScoreAsync());

        // A second clip created already attached to the objective → +2 more.
        await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 7,
            SourceKey: "clip:7",
            StartTimeSeconds: 200,
            EndTimeSeconds: 220,
            Title: "Good roam",
            ObjectiveId: objectiveId,
            Status: EvidenceStatuses.Evidence));
        Assert.Equal(4, await ScoreAsync());

        // Re-upserting that same clip (same source key) must not award again.
        await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 7,
            SourceKey: "clip:7",
            StartTimeSeconds: 201,
            EndTimeSeconds: 221,
            Title: "Good roam (renamed)",
            ObjectiveId: objectiveId,
            Status: EvidenceStatuses.Evidence));
        Assert.Equal(4, await ScoreAsync());
    }

    [Fact]
    public async Task UpsertAsync_WithSameSourceKey_ReusesCandidateWithoutClobberingUserStatus()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Kai'Sa", true);
        var firstId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 10,
            SourceKey: "clip:10",
            StartTimeSeconds: 100,
            EndTimeSeconds: 120,
            Title: "Saved clip",
            Status: EvidenceStatuses.NeedsReview));

        await scope.Evidence.UpdateStatusAsync(firstId, EvidenceStatuses.Highlight);

        var secondId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 10,
            SourceKey: "clip:10",
            StartTimeSeconds: 101,
            EndTimeSeconds: 121,
            Title: "Saved clip renamed",
            Status: EvidenceStatuses.NeedsReview));

        var row = Assert.Single(await scope.Evidence.GetForGameAsync(gameId));
        Assert.Equal(firstId, secondId);
        Assert.Equal(EvidenceStatuses.Highlight, row.Status);
        Assert.Equal("Saved clip renamed", row.Title);
    }

    [Fact]
    public async Task PromptId_RoundTripsThroughUpsertAndUpdate_IndependentlyOfObjective()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objectiveId = await scope.Objectives.CreateAsync("Roam after pushing", "macro");
        var promptId = await scope.Prompts.CreatePromptAsync(
            objectiveId, ObjectivePhases.InGame, "Did you ping before roaming?", 0);
        var otherPromptId = await scope.Prompts.CreatePromptAsync(
            objectiveId, ObjectivePhases.InGame, "Was the wave pushing?", 1);

        // Upsert a clip already tagged to BOTH an objective and a prompt.
        var evidenceId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 5,
            SourceKey: "clip:5",
            StartTimeSeconds: 300,
            EndTimeSeconds: 320,
            Title: "Roam clip",
            ObjectiveId: objectiveId,
            PromptId: promptId,
            Status: EvidenceStatuses.Evidence));

        var row = Assert.Single(await scope.Evidence.GetForGameAsync(gameId));
        Assert.Equal(promptId, row.PromptId);
        Assert.Equal(objectiveId, row.ObjectiveId);

        // Re-tag to a different prompt — objective must stay untouched.
        await scope.Evidence.UpdatePromptAsync(evidenceId, otherPromptId);
        row = Assert.Single(await scope.Evidence.GetForGameAsync(gameId));
        Assert.Equal(otherPromptId, row.PromptId);
        Assert.Equal(objectiveId, row.ObjectiveId);

        // Detach the prompt (null) — objective still coexists.
        await scope.Evidence.UpdatePromptAsync(evidenceId, null);
        row = Assert.Single(await scope.Evidence.GetForGameAsync(gameId));
        Assert.Null(row.PromptId);
        Assert.Equal(objectiveId, row.ObjectiveId);

        // Untagged-by-default: an upsert with no PromptId leaves it null.
        var untaggedId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.TimelineRegion,
            SourceId: null,
            SourceKey: "auto-moment-1",
            StartTimeSeconds: 100,
            EndTimeSeconds: 120,
            Title: "Auto moment"));
        var untagged = (await scope.Evidence.GetForGameAsync(gameId))
            .Single(r => r.Id == untaggedId);
        Assert.Null(untagged.PromptId);
        Assert.Null(untagged.ObjectiveId);
    }

    [Fact]
    public async Task UpdatePromptAsync_DoesNotAwardObjectiveScore()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var gameId = await scope.Games.SaveManualAsync("Ahri", true);
        var objectiveId = await scope.Objectives.CreateAsync("Roam after pushing", "macro");
        var promptId = await scope.Prompts.CreatePromptAsync(
            objectiveId, ObjectivePhases.InGame, "Did you ping?", 0);

        var evidenceId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: gameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 9,
            SourceKey: "clip:9",
            StartTimeSeconds: 150,
            EndTimeSeconds: 170,
            Title: "Some clip"));

        // Prompt tagging is organizational only — score stays at 0 (objective_id
        // is the score-bearing path, exercised by the score test above).
        await scope.Evidence.UpdatePromptAsync(evidenceId, promptId);
        var obj = await scope.Objectives.GetAsync(objectiveId);
        Assert.Equal(0, obj!.Score);
    }

    [Fact]
    public async Task GetPatternCardsAsync_FindsDeterministicFailurePatterns()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync("Reset before dragon", "macro");
        var gameId = await scope.Games.SaveManualAsync("Ahri", false);

        for (var i = 0; i < 3; i++)
        {
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: gameId,
                SourceKind: EvidenceKinds.TimelineRegion,
                SourceId: null,
                SourceKey: $"death:{i}",
                StartTimeSeconds: 600 + i * 120,
                EndTimeSeconds: 610 + i * 120,
                Title: "Isolated death",
                ObjectiveId: objectiveId,
                Polarity: EvidencePolarities.Bad));
        }

        for (var i = 0; i < 2; i++)
        {
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: gameId,
                SourceKind: EvidenceKinds.TimelineRegion,
                SourceId: null,
                SourceKey: $"dragon:{i}",
                StartTimeSeconds: 900 + i * 300,
                EndTimeSeconds: 930 + i * 300,
                Title: "Lost Dragon fight",
                ObjectiveId: objectiveId,
                Polarity: EvidencePolarities.Bad));
        }

        for (var i = 0; i < 2; i++)
        {
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: gameId,
                SourceKind: EvidenceKinds.TimelineRegion,
                SourceId: null,
                SourceKey: $"pre-objective-death:{i}",
                StartTimeSeconds: 1200 + i * 300,
                EndTimeSeconds: 1240 + i * 300,
                Title: "Death before Dragon",
                ObjectiveId: objectiveId,
                Polarity: EvidencePolarities.Bad));
        }

        var cards = await scope.Evidence.GetPatternCardsAsync();

        Assert.Contains(cards, card => card.Kind == "isolated_deaths");
        Assert.Contains(cards, card => card.Kind == "lost_objective_fights");
        Assert.Contains(cards, card => card.Kind == "deaths_before_objectives");
        Assert.Contains(cards, card => card.Kind == "bad_objective_evidence" && card.ObjectiveId == objectiveId);
    }

    [Fact]
    public async Task GetPatternCardsAsync_ExcludesEvidenceFromReviewedGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync("Stay in line with support", "laning");
        var gameId = await scope.Games.SaveManualAsync("Kai'Sa", false);

        for (var i = 0; i < 2; i++)
        {
            await scope.Evidence.UpsertAsync(new EvidenceUpsert(
                GameId: gameId,
                SourceKind: EvidenceKinds.Clip,
                SourceId: i + 1,
                SourceKey: $"clip:{i + 1}",
                StartTimeSeconds: 40 + i * 60,
                EndTimeSeconds: 55 + i * 60,
                Title: "Bad spacing with support",
                ObjectiveId: objectiveId,
                Polarity: EvidencePolarities.Bad,
                Status: EvidenceStatuses.Evidence));
        }

        Assert.Contains(
            await scope.Evidence.GetPatternCardsAsync(),
            card => card.Kind == "bad_objective_evidence" && card.ObjectiveId == objectiveId);

        await scope.Games.UpdateReviewAsync(gameId, new GameReview
        {
            Rating = 4,
            Notes = "Reviewed the lane spacing clips."
        });

        Assert.DoesNotContain(
            await scope.Evidence.GetPatternCardsAsync(),
            card => card.Kind == "bad_objective_evidence" && card.ObjectiveId == objectiveId);
    }
}
