using Revu.Core.Data.Repositories;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Contract tests for the review-page write slices the sidecar exposes:
///   • death classification  (one-tap cause taxonomy → death_classifications)
///   • evidence triage        (needs_review → evidence/highlight, polarity, objective link)
///
/// These exercise the EXACT Revu.Core persistence code the sidecar's
/// set_death_class / set_evidence_status / set_evidence_objective endpoints run,
/// through the shared SidecarWriteScope harness (real temp SQLite, real schema).
///
/// The clobber concern called out in the death-audit memory (InsertEventAsync vs
/// SaveEventsAsync wiping prior rows) is asserted structurally here: a second
/// classification at a DIFFERENT game_time must INSERT a new row, never overwrite
/// the first — death_classifications is keyed UNIQUE(game_id, game_time_s), so
/// the upsert only collides when the (game, second) pair matches exactly.
/// </summary>
public sealed class DeathAndEvidenceWriteTests
{
    // ── Death classification (one-tap cause taxonomy) ───────────────────────

    [Fact]
    public async Task DeathClassification_RoundTrips_ThroughTheWriteSlice()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 7001, champion: "Ahri");

        await scope.DeathClassifications.UpsertAsync(game.GameId, 312, DeathClasses.Vision);

        var rows = await scope.DeathClassifications.GetForGameAsync(game.GameId);
        var row = Assert.Single(rows);
        Assert.Equal(game.GameId, row.GameId);
        Assert.Equal(312, row.GameTimeSeconds);
        Assert.Equal(DeathClasses.Vision, row.DeathClass);
        Assert.NotNull(row.CreatedAt);
    }

    [Fact]
    public async Task DeathClassification_Reclassify_UpdatesInPlace_DoesNotDuplicate()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 7002);

        // First tap: classify the death at second 845.
        await scope.DeathClassifications.UpsertAsync(game.GameId, 845, DeathClasses.Greed);
        // Re-tap the SAME death (same game, same second) with a different cause.
        await scope.DeathClassifications.UpsertAsync(game.GameId, 845, DeathClasses.Tempo);

        var rows = await scope.DeathClassifications.GetForGameAsync(game.GameId);
        var row = Assert.Single(rows); // upsert on UNIQUE(game_id, game_time_s) → exactly one row
        Assert.Equal(DeathClasses.Tempo, row.DeathClass);
    }

    [Fact]
    public async Task DeathClassification_SecondDeathDifferentTime_DoesNotClobberFirst()
    {
        // This is the structural guard against the InsertEventAsync-vs-
        // SaveEventsAsync clobber pattern: classifying a SECOND death must not
        // erase the first when the game_time differs.
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 7003);

        await scope.DeathClassifications.UpsertAsync(game.GameId, 312, DeathClasses.Vision);
        await scope.DeathClassifications.UpsertAsync(game.GameId, 845, DeathClasses.Greed);
        await scope.DeathClassifications.UpsertAsync(game.GameId, 1290, DeathClasses.Wave);

        var rows = await scope.DeathClassifications.GetForGameAsync(game.GameId);

        Assert.Equal(3, rows.Count);
        // GetForGameAsync orders by game_time_s ASC, so the earliest death is first
        // and is intact after the later writes — no clobber.
        Assert.Equal(312, rows[0].GameTimeSeconds);
        Assert.Equal(DeathClasses.Vision, rows[0].DeathClass);
        Assert.Equal(845, rows[1].GameTimeSeconds);
        Assert.Equal(DeathClasses.Greed, rows[1].DeathClass);
        Assert.Equal(1290, rows[2].GameTimeSeconds);
        Assert.Equal(DeathClasses.Wave, rows[2].DeathClass);
    }

    [Theory]
    [InlineData("VISION", DeathClasses.Vision)]
    [InlineData("  Greed  ", DeathClasses.Greed)]
    [InlineData("CoOlDoWnS", DeathClasses.Cooldowns)]
    public async Task DeathClassification_NormalizesCaseAndWhitespace_OnWrite(string input, string expected)
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 7004);

        // The UI/endpoint may pass a chip label or padded value; the write slice
        // must persist the canonical lowercase key so GetClassMixAsync groups it.
        await scope.DeathClassifications.UpsertAsync(game.GameId, 500, input);

        var row = Assert.Single(await scope.DeathClassifications.GetForGameAsync(game.GameId));
        Assert.Equal(expected, row.DeathClass);
    }

    [Fact]
    public async Task DeathClassification_Clear_RemovesOnlyThatDeath()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 7005);
        await scope.DeathClassifications.UpsertAsync(game.GameId, 312, DeathClasses.Vision);
        await scope.DeathClassifications.UpsertAsync(game.GameId, 845, DeathClasses.Greed);

        await scope.DeathClassifications.ClearAsync(game.GameId, 845);

        var row = Assert.Single(await scope.DeathClassifications.GetForGameAsync(game.GameId));
        Assert.Equal(312, row.GameTimeSeconds);
        Assert.Equal(DeathClasses.Vision, row.DeathClass);
    }

    [Fact]
    public async Task DeathClassMix_AggregatesAcrossClassifiedDeaths()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 7006); // timestamp = now → inside window

        await scope.DeathClassifications.UpsertAsync(game.GameId, 100, DeathClasses.Vision);
        await scope.DeathClassifications.UpsertAsync(game.GameId, 200, DeathClasses.Vision);
        await scope.DeathClassifications.UpsertAsync(game.GameId, 300, DeathClasses.Greed);

        var mix = await scope.DeathClassifications.GetClassMixAsync(days: 14);

        Assert.Equal(3, mix.Sum(m => m.Count));
        // Ordered by count DESC → the dominant bucket (vision, 2) leads.
        Assert.Equal(DeathClasses.Vision, mix[0].DeathClass);
        Assert.Equal(2, mix[0].Count);
    }

    // ── Evidence triage (needs_review → evidence/highlight, polarity, link) ──

    [Fact]
    public async Task Evidence_NewItem_DefaultsToNeedsReviewAndNeutral()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 8001);

        var evidenceId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: game.GameId,
            SourceKind: EvidenceKinds.TimelineRegion,
            SourceId: null,
            SourceKey: "moment-900-940",
            StartTimeSeconds: 900,
            EndTimeSeconds: 940,
            Title: "Lost Dragon fight"));

        var row = Assert.Single(await scope.Evidence.GetForGameAsync(game.GameId));
        Assert.Equal(evidenceId, row.Id);
        Assert.Equal(EvidenceStatuses.NeedsReview, row.Status);
        Assert.Equal(EvidencePolarities.Neutral, row.Polarity);
        Assert.Null(row.ObjectiveId);
        Assert.Equal(900, row.StartTimeSeconds);
        Assert.Equal(940, row.EndTimeSeconds);
    }

    [Fact]
    public async Task Evidence_TriageTransition_NeedsReviewToEvidence_Persists()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 8002);
        var objectiveId = await scope.Objectives.CreateAsync("Hold wave before objective", "macro");

        var evidenceId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: game.GameId,
            SourceKind: EvidenceKinds.TimelineRegion,
            SourceId: null,
            SourceKey: "triage-1",
            StartTimeSeconds: 600,
            EndTimeSeconds: 640,
            Title: "Caught out before Dragon"));

        // Triage: link an objective, set polarity, promote out of the queue.
        await scope.Evidence.UpdateObjectiveAsync(evidenceId, objectiveId);
        await scope.Evidence.UpdatePolarityAsync(evidenceId, EvidencePolarities.Bad);
        await scope.Evidence.UpdateStatusAsync(evidenceId, EvidenceStatuses.Evidence);

        var row = Assert.Single(await scope.Evidence.GetForGameAsync(game.GameId));
        Assert.Equal(objectiveId, row.ObjectiveId);
        Assert.Equal("Hold wave before objective", row.ObjectiveTitle);
        Assert.Equal(EvidencePolarities.Bad, row.Polarity);
        Assert.Equal(EvidenceStatuses.Evidence, row.Status);
    }

    [Fact]
    public async Task Evidence_Dismiss_HidesFromDefaultQueueButKeepsRow()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 8003);
        var evidenceId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: game.GameId,
            SourceKind: EvidenceKinds.TimelineRegion,
            SourceId: null,
            SourceKey: "dismiss-1",
            StartTimeSeconds: 120,
            EndTimeSeconds: 140,
            Title: "Not actually a mistake"));

        await scope.Evidence.UpdateStatusAsync(evidenceId, EvidenceStatuses.Dismissed);

        // Dismiss is a soft hide — the default query drops it, includeDismissed keeps it.
        Assert.Empty(await scope.Evidence.GetForGameAsync(game.GameId));
        var hidden = Assert.Single(await scope.Evidence.GetForGameAsync(game.GameId, includeDismissed: true));
        Assert.Equal(EvidenceStatuses.Dismissed, hidden.Status);
    }

    [Fact]
    public async Task Evidence_PendingCount_ReflectsOnlyNeedsReview()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 8004);

        var keepPending = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: game.GameId, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
            SourceKey: "pending-1", StartTimeSeconds: 10, EndTimeSeconds: 20, Title: "A"));
        var triaged = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: game.GameId, SourceKind: EvidenceKinds.TimelineRegion, SourceId: null,
            SourceKey: "pending-2", StartTimeSeconds: 30, EndTimeSeconds: 40, Title: "B"));

        Assert.Equal(2, await scope.Evidence.CountPendingAsync());

        await scope.Evidence.UpdateStatusAsync(triaged, EvidenceStatuses.Evidence);
        Assert.Equal(1, await scope.Evidence.CountPendingAsync());

        // The still-pending row is the one we never triaged.
        var forGame = await scope.Evidence.GetForGameAsync(game.GameId);
        var pendingRow = Assert.Single(forGame, e => e.Status == EvidenceStatuses.NeedsReview);
        Assert.Equal(keepPending, pendingRow.Id);
    }

    [Fact]
    public async Task Evidence_ReUpsertSameSourceKey_DoesNotClobberTriagedStatus()
    {
        // The endpoint may re-emit the same auto-detected moment (same source_key)
        // after the user has already triaged it. The write slice must NOT demote a
        // user-set status back to needs_review — the conditional UPDATE guards this.
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 8005);

        var firstId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: game.GameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 10,
            SourceKey: "clip:10",
            StartTimeSeconds: 100,
            EndTimeSeconds: 120,
            Title: "Saved clip",
            Status: EvidenceStatuses.NeedsReview));

        await scope.Evidence.UpdateStatusAsync(firstId, EvidenceStatuses.Highlight);
        await scope.Evidence.UpdatePolarityAsync(firstId, EvidencePolarities.Good);

        // Re-emit the same source_key as a fresh needs_review/neutral upsert.
        var secondId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: game.GameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 10,
            SourceKey: "clip:10",
            StartTimeSeconds: 101,
            EndTimeSeconds: 121,
            Title: "Saved clip (renamed)",
            Status: EvidenceStatuses.NeedsReview));

        var row = Assert.Single(await scope.Evidence.GetForGameAsync(game.GameId));
        Assert.Equal(firstId, secondId); // reused, not duplicated
        Assert.Equal(EvidenceStatuses.Highlight, row.Status);   // user status preserved
        Assert.Equal(EvidencePolarities.Good, row.Polarity);    // user polarity preserved
        Assert.Equal("Saved clip (renamed)", row.Title);        // metadata DID refresh
    }

    [Fact]
    public async Task Evidence_LinkObjective_IsRetrievableByObjective()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 8006);
        var objectiveId = await scope.Objectives.CreateAsync("Track jungler before recall", "macro");

        var evidenceId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: game.GameId,
            SourceKind: EvidenceKinds.Clip,
            SourceId: 5,
            SourceKey: "clip:5",
            StartTimeSeconds: 400,
            EndTimeSeconds: 420,
            Title: "Recalled into a gank",
            ObjectiveId: objectiveId,
            Polarity: EvidencePolarities.Bad,
            Status: EvidenceStatuses.Evidence));

        var byObjective = Assert.Single(await scope.Evidence.GetForObjectiveAsync(objectiveId));
        Assert.Equal(evidenceId, byObjective.Id);
        Assert.Equal(objectiveId, byObjective.ObjectiveId);
    }

    [Fact]
    public async Task Evidence_AttachingClipToObjective_AwardsTwoPoints_OnceOnly()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        var game = await scope.SeedGameAsync(gameId: 8007);
        var objectiveId = await scope.Objectives.CreateAsync("Roam after pushing", "macro");

        async Task<int> ScoreAsync() => (await scope.Objectives.GetAsync(objectiveId))!.Score;

        Assert.Equal(0, await ScoreAsync());

        var evidenceId = await scope.Evidence.UpsertAsync(new EvidenceUpsert(
            GameId: game.GameId,
            SourceKind: EvidenceKinds.TimelineRegion,
            SourceId: null,
            SourceKey: "roam-1",
            StartTimeSeconds: 100,
            EndTimeSeconds: 120,
            Title: "Missed roam"));

        await scope.Evidence.UpdateObjectiveAsync(evidenceId, objectiveId);
        Assert.Equal(2, await ScoreAsync()); // +2 for attaching footage to the focus

        // Re-linking the SAME objective must not stack the bonus.
        await scope.Evidence.UpdateObjectiveAsync(evidenceId, objectiveId);
        Assert.Equal(2, await ScoreAsync());
    }
}
