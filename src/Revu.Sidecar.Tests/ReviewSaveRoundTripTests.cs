using Xunit;
using Revu.Core.Data.Repositories;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Sidecar.Tests;

/// <summary>
/// save_review FULL round-trip + the null-passthrough no-clobber contract.
///
/// This is the highest-value sidecar-write contract: the save_review endpoint
/// delegates to ReviewWorkflowService.SaveAsync, where the two sev-2
/// save-clobber data-loss defects lived (the Tauri form omits ImprovementNote /
/// MentalHandled / the three attribution texts / EnemyLaner / MatchupNote, so
/// they arrive null — null MUST mean "leave the persisted value unchanged",
/// never wipe migrated/pre-existing data). These tests exercise the REAL
/// persistence code the endpoint runs against a throwaway SQLite DB via
/// SidecarWriteScope.
/// </summary>
public sealed class ReviewSaveRoundTripTests
{
    // A baseline snapshot with every nullable field set to "" (i.e. the WinUI /
    // a fully-rendered form payload). Individual tests override what they need.
    private static readonly ReviewSnapshot EmptySnapshot = new(
        MentalRating: 5,
        WentWell: "",
        Mistakes: "",
        FocusNext: "",
        ReviewNotes: "",
        ImprovementNote: "",
        Attribution: "",
        MentalHandled: "",
        SpottedProblems: "",
        OutsideControl: "",
        WithinControl: "",
        PersonalContribution: "",
        EnemyLaner: "",
        MatchupNote: "",
        SelectedTagIds: [],
        ObjectivePractices: []);

    [Fact]
    public async Task SaveAsync_FullSnapshot_EveryFieldRoundTripsThroughTheDb()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        const long gameId = 7001;
        await scope.SeedGameAsync(gameId, champion: "Orianna", win: false);

        // Two real objectives + two real catalog tags so the practice/tag joins
        // exercise actual rows, not placeholders.
        var objectiveId = await scope.Objectives.CreateAsync(
            "Hold TP window", "macro", completionCriteria: "Crash before roam");
        var secondObjectiveId = await scope.Objectives.CreateAsync(
            "Track jungle", "vision", completionCriteria: "Ping on each show");
        // Use names that do NOT collide with the seeded default catalog tags
        // (Schema.DefaultConceptTags). CreateAsync uses INSERT OR IGNORE and
        // returns last_insert_rowid(), so a name collision would no-op and hand
        // back a stale/duplicate id — distinct names guarantee two distinct rows.
        var tagId = await scope.ConceptTags.CreateAsync("RoundTrip negative tag", "negative");
        var secondTagId = await scope.ConceptTags.CreateAsync("RoundTrip positive tag", "positive");
        Assert.NotEqual(tagId, secondTagId);

        var snapshot = EmptySnapshot with
        {
            MentalRating = 7,
            WentWell = "Played waves well",
            Mistakes = "Missed one flash punish",
            FocusNext = "Track jungle timers",
            ReviewNotes = "Strong reset discipline overall",
            ImprovementNote = "Keep lane warded",
            Attribution = "Own mistakes",
            MentalHandled = "Reset after death",
            SpottedProblems = "Late swap to side lane",
            OutsideControl = "Enemy jungle pathing",
            WithinControl = "Wave state",
            PersonalContribution = "Did not cover flank",
            EnemyLaner = "Syndra",
            MatchupNote = "Respect level 3 burst",
            SelectedTagIds = [tagId, secondTagId],
            ObjectivePractices =
            [
                new SaveObjectivePracticeRequest(objectiveId, true, "Held wave before base"),
                new SaveObjectivePracticeRequest(secondObjectiveId, false, "Lost track at 6"),
            ],
            FocusAdherence = 2,
        };

        var result = await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            GameId: gameId,
            ChampionName: "Orianna",
            Win: false,
            RequireReviewNotes: false,
            Snapshot: snapshot));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("Syndra", result.SavedEnemyLaner);

        // ── games row: review prose + control texts + enemy laner ──────────
        var game = await scope.Games.GetAsync(gameId);
        Assert.NotNull(game);
        Assert.Equal("Strong reset discipline overall", game!.ReviewNotes);
        Assert.Equal("Played waves well", game.WentWell);
        Assert.Equal("Missed one flash punish", game.Mistakes);
        Assert.Equal("Track jungle timers", game.FocusNext);
        Assert.Equal("Late swap to side lane", game.SpottedProblems);
        Assert.Equal("Enemy jungle pathing", game.OutsideControl);
        Assert.Equal("Wave state", game.WithinControl);
        Assert.Equal("Own mistakes", game.Attribution);
        Assert.Equal("Did not cover flank", game.PersonalContribution);
        Assert.Equal("Syndra", game.EnemyLaner);

        // ── session_log row: mental rating, improvement note, mental handled,
        //    focus adherence ─────────────────────────────────────────────────
        var entry = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.NotNull(entry);
        Assert.Equal(7, entry!.MentalRating);
        Assert.Equal("Keep lane warded", entry.ImprovementNote);
        Assert.Equal("Reset after death", entry.MentalHandled);
        Assert.Equal(2, entry.FocusAdherence);

        // ── concept tags ───────────────────────────────────────────────────
        var tagIds = await scope.ConceptTags.GetIdsForGameAsync(gameId);
        Assert.Equal(2, tagIds.Count);
        Assert.Contains(tagId, tagIds);
        Assert.Contains(secondTagId, tagIds);

        // ── matchup note ───────────────────────────────────────────────────
        var matchupNote = await scope.MatchupNotes.GetForGameAsync(gameId);
        Assert.NotNull(matchupNote);
        Assert.Equal("Respect level 3 burst", matchupNote!.Note);
        Assert.Equal("Syndra", matchupNote.Enemy);

        // ── objective practices ────────────────────────────────────────────
        var objectives = await scope.Objectives.GetGameObjectivesAsync(gameId);
        Assert.Equal(2, objectives.Count);

        var practiced = Assert.Single(objectives, o => o.ObjectiveId == objectiveId);
        Assert.True(practiced.Practiced);
        Assert.Equal("Held wave before base", practiced.ExecutionNote);

        var notPracticed = Assert.Single(objectives, o => o.ObjectiveId == secondObjectiveId);
        Assert.False(notPracticed.Practiced);
        Assert.Equal("Lost track at 6", notPracticed.ExecutionNote);
    }

    [Fact]
    public async Task SaveAsync_NullableFields_OnSecondSave_DoNotClobberPersistedValues()
    {
        // THE save-clobber regression. First save writes concrete values to the
        // seven null-passthrough fields. Second save sends those SAME fields as
        // null (the Tauri debrief form that omits them). The first-save values
        // MUST survive — null = unchanged, never a wipe.
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        const long gameId = 7002;
        await scope.SeedGameAsync(gameId, champion: "Sivir", win: true);

        // 1) Full save: populate the seven fields the Tauri form doesn't render.
        var fullSave = await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            GameId: gameId,
            ChampionName: "Sivir",
            Win: true,
            RequireReviewNotes: false,
            Snapshot: EmptySnapshot with
            {
                MentalRating = 6,
                OutsideControl = "karma griefing the dive",
                WithinControl = "held flash too long",
                PersonalContribution = "overstayed the wave",
                ImprovementNote = "ward the dive path",
                MentalHandled = "reset after the int",
                EnemyLaner = "Ezreal",
                MatchupNote = "block E for his Q",
            }));
        Assert.True(fullSave.Success, fullSave.ErrorMessage);

        // 2) Debrief-only re-save: the seven omitted fields arrive NULL.
        var partialSave = await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            GameId: gameId,
            ChampionName: "Sivir",
            Win: true,
            RequireReviewNotes: false,
            Snapshot: EmptySnapshot with
            {
                MentalRating = 6,
                WentWell = "good early trades", // a rendered field DOES update
                OutsideControl = null,
                WithinControl = null,
                PersonalContribution = null,
                ImprovementNote = null,
                MentalHandled = null,
                EnemyLaner = null,
                MatchupNote = null,
            }));
        Assert.True(partialSave.Success, partialSave.ErrorMessage);

        // The save result reports the enemy laner unchanged, not blanked.
        Assert.Equal("Ezreal", partialSave.SavedEnemyLaner);

        // 3) Everything the form omitted survived.
        var game = await scope.Games.GetAsync(gameId);
        Assert.NotNull(game);
        Assert.Equal("karma griefing the dive", game!.OutsideControl);
        Assert.Equal("held flash too long", game.WithinControl);
        Assert.Equal("overstayed the wave", game.PersonalContribution);
        Assert.Equal("Ezreal", game.EnemyLaner);

        var entry = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.NotNull(entry);
        Assert.Equal("ward the dive path", entry!.ImprovementNote);
        Assert.Equal("reset after the int", entry.MentalHandled);

        var matchupNote = await scope.MatchupNotes.GetForGameAsync(gameId);
        Assert.NotNull(matchupNote);
        Assert.Equal("block E for his Q", matchupNote!.Note);
    }

    [Fact]
    public async Task SaveAsync_NonNullableProseFields_OnSecondSave_DoOverwrite()
    {
        // The flip side of the no-clobber contract: the prose fields the Tauri
        // form OWNS (went_well / mistakes / focus_next / review_notes /
        // spotted_problems / attribution) are non-nullable, so a second save
        // MUST overwrite them with the new (here: cleared) values. They are not
        // protected by null=unchanged — the form always sends them.
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        const long gameId = 7003;
        await scope.SeedGameAsync(gameId, champion: "Ahri", win: true);

        // 1) First save populates the form-owned prose fields.
        var first = await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            GameId: gameId,
            ChampionName: "Ahri",
            Win: true,
            RequireReviewNotes: false,
            Snapshot: EmptySnapshot with
            {
                MentalRating = 8,
                WentWell = "first went well",
                Mistakes = "first mistakes",
                FocusNext = "first focus",
                ReviewNotes = "first notes",
                SpottedProblems = "first problems",
                Attribution = "first attribution",
            }));
        Assert.True(first.Success, first.ErrorMessage);

        // 2) Second save changes them (overwrite) and clears two (empty string,
        //    which the form DID submit — an explicit clear, not omission).
        var second = await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            GameId: gameId,
            ChampionName: "Ahri",
            Win: true,
            RequireReviewNotes: false,
            Snapshot: EmptySnapshot with
            {
                MentalRating = 8,
                WentWell = "second went well",
                Mistakes = "",                 // explicit clear
                FocusNext = "second focus",
                ReviewNotes = "",              // explicit clear
                SpottedProblems = "second problems",
                Attribution = "second attribution",
            }));
        Assert.True(second.Success, second.ErrorMessage);

        var game = await scope.Games.GetAsync(gameId);
        Assert.NotNull(game);
        Assert.Equal("second went well", game!.WentWell);
        Assert.Equal("", game.Mistakes);                 // overwritten to empty
        Assert.Equal("second focus", game.FocusNext);
        Assert.Equal("", game.ReviewNotes);              // overwritten to empty
        Assert.Equal("second problems", game.SpottedProblems);
        Assert.Equal("second attribution", game.Attribution);
    }

    [Fact]
    public async Task SaveAsync_ExplicitEmptyString_OnNullableField_StillOverwrites()
    {
        // Guards against over-correcting the null=unchanged fix into "empty never
        // writes". A NON-null empty string on a nullable field is an explicit
        // clear (a cleared textarea the form submitted) and MUST overwrite, while
        // null on a sibling field leaves it unchanged.
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        const long gameId = 7004;
        await scope.SeedGameAsync(gameId, champion: "Sivir", win: true);

        await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            gameId, "Sivir", true, false,
            EmptySnapshot with { OutsideControl = "something", WithinControl = "else" }));

        var clear = await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            gameId, "Sivir", true, false,
            EmptySnapshot with { OutsideControl = "", WithinControl = null }));
        Assert.True(clear.Success, clear.ErrorMessage);

        var game = await scope.Games.GetAsync(gameId);
        Assert.NotNull(game);
        Assert.Equal("", game!.OutsideControl);   // explicit "" overwrote
        Assert.Equal("else", game.WithinControl); // null left it unchanged
    }

    [Fact]
    public async Task SaveAsync_FocusAdherence_ReSentOnEverySave_RoundTripsAcrossSaves()
    {
        // FocusAdherence is its OWN seam and does NOT follow the null=unchanged
        // no-clobber contract the other nullable fields use. It is a tri-state
        // answer (null=unanswered, 0=no, 1=partly, 2=yes), and
        // ReviewWorkflowService.SaveAsync (ReviewWorkflowService.cs:245) calls
        // UpdateFocusAdherenceAsync(request.Snapshot.FocusAdherence)
        // UNCONDITIONALLY — the underlying repo (SessionLogRepository:397-407)
        // writes DBNull on a null. So preservation is the CALLER's job: the live
        // form hydrates the current adherence and re-sends it on every save.
        //
        // (The null-wipes-to-unanswered behavior is asserted directly by
        // FocusAdherenceContractTests.SaveAsync_NullFocusAdherence_PersistsAsUnanswered;
        // this test pins the realistic round-trip: re-sending the current value
        // on a follow-up save preserves the recorded answer.)
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        const long gameId = 7005;
        await scope.SeedGameAsync(gameId, champion: "Ahri", win: true);

        // First save records adherence = 2 ("yes").
        var first = await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            gameId, "Ahri", true, false,
            EmptySnapshot with { MentalRating = 7, FocusAdherence = 2 }));
        Assert.True(first.Success, first.ErrorMessage);

        var afterFirst = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.NotNull(afterFirst);
        Assert.Equal(2, afterFirst!.FocusAdherence);

        // Second save re-sends the current adherence (as the hydrated form does).
        // The recorded answer survives.
        var second = await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            gameId, "Ahri", true, false,
            EmptySnapshot with { MentalRating = 7, FocusAdherence = 2 }));
        Assert.True(second.Success, second.ErrorMessage);

        var afterSecond = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.NotNull(afterSecond);
        Assert.Equal(2, afterSecond!.FocusAdherence);
    }
}
