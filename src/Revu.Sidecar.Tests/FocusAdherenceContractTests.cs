using Revu.Core.Models;
using Revu.Core.Services;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// Contract tests for the P-028 defect class: the one-tap "did you do your
/// focus?" answer (session_log.focus_adherence) must round-trip through the
/// sidecar write seam intact.
///
/// Encoding (see SessionLogEntry.FocusAdherence + ReviewSnapshot.FocusAdherence):
///   null = unanswered, 0 = no, 1 = partly, 2 = yes.
///
/// Two write paths exist and both are exercised here:
///   1. SessionLogRepository.UpdateFocusAdherenceAsync  (the direct stamp the
///      one-tap dashboard control hits).
///   2. ReviewWorkflowService.SaveAsync, which calls UpdateFocusAdherenceAsync
///      UNCONDITIONALLY with Snapshot.FocusAdherence after LogGameAsync has
///      guaranteed the session_log row.
///
/// Read-back is via SessionLogRepository.GetEntryAsync(gameId).FocusAdherence.
/// </summary>
public sealed class FocusAdherenceContractTests
{
    // A baseline snapshot that satisfies the save path without touching any of
    // the null=unchanged fields. FocusAdherence is overridden per test.
    private static ReviewSnapshot Snapshot(int? focusAdherence) => new(
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
        ObjectivePractices: [],
        FocusAdherence: focusAdherence);

    // ── Direct repository path: UpdateFocusAdherenceAsync ─────────────────────

    [Theory]
    [InlineData(2)] // yes
    [InlineData(1)] // partly
    [InlineData(0)] // no
    public async Task UpdateFocusAdherence_PersistsEachAnswer_AndRoundTrips(int adherence)
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        const long gameId = 7101;
        await scope.SeedGameAsync(gameId);
        // The direct UPDATE is a no-op without an existing session_log row, so
        // create the row first (mirrors the live flow where LogGameAsync runs).
        await scope.SessionLog.LogGameAsync(gameId, "Ahri", win: true);

        await scope.SessionLog.UpdateFocusAdherenceAsync(gameId, adherence);

        var entry = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.NotNull(entry);
        Assert.Equal(adherence, entry!.FocusAdherence);
    }

    [Fact]
    public async Task UpdateFocusAdherence_Null_RoundTripsAsUnanswered()
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        const long gameId = 7102;
        await scope.SeedGameAsync(gameId);
        await scope.SessionLog.LogGameAsync(gameId, "Ahri", win: true);

        // First stamp a concrete answer, then clear it back to unanswered.
        await scope.SessionLog.UpdateFocusAdherenceAsync(gameId, 2);
        var stamped = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.Equal(2, stamped!.FocusAdherence);

        await scope.SessionLog.UpdateFocusAdherenceAsync(gameId, null);

        var cleared = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.NotNull(cleared);
        Assert.Null(cleared!.FocusAdherence);
    }

    [Fact]
    public async Task FreshSessionLogRow_HasUnansweredFocusAdherence()
    {
        // A row created by LogGameAsync without any adherence stamp must read
        // back as null (unanswered), not 0 (no). The two are distinct answers.
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        const long gameId = 7103;
        await scope.SeedGameAsync(gameId);
        await scope.SessionLog.LogGameAsync(gameId, "Ahri", win: true);

        var entry = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.NotNull(entry);
        Assert.Null(entry!.FocusAdherence);
    }

    [Fact]
    public async Task UpdateFocusAdherence_NoSessionLogRow_IsNoOp()
    {
        // The direct stamp is documented to no-op when the game has no
        // session_log row yet (the review save re-stamps after LogGameAsync
        // creates it). Verify it neither throws nor materializes a row.
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        const long gameId = 7104;
        await scope.SeedGameAsync(gameId);

        await scope.SessionLog.UpdateFocusAdherenceAsync(gameId, 2);

        var entry = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.Null(entry); // no session_log row was created by the stamp alone
    }

    [Fact]
    public async Task UpdateFocusAdherence_OutOfRangeValue_ClampsToZeroTwo()
    {
        // The repo clamps to [0,2] (Math.Clamp). A stray 5 must persist as 2
        // (yes), and a negative as 0 (no) — the read must never surface an
        // out-of-domain code.
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        const long gameId = 7105;
        await scope.SeedGameAsync(gameId);
        await scope.SessionLog.LogGameAsync(gameId, "Ahri", win: true);

        await scope.SessionLog.UpdateFocusAdherenceAsync(gameId, 5);
        var high = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.Equal(2, high!.FocusAdherence);

        await scope.SessionLog.UpdateFocusAdherenceAsync(gameId, -3);
        var low = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.Equal(0, low!.FocusAdherence);
    }

    // ── Save path: ReviewWorkflowService.SaveAsync ────────────────────────────

    [Theory]
    [InlineData(2)] // yes
    [InlineData(1)] // partly
    [InlineData(0)] // no
    public async Task SaveAsync_PersistsFocusAdherence_AndReReadReturnsIt(int adherence)
    {
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        const long gameId = 7201;
        await scope.SeedGameAsync(gameId, champion: "Ahri", win: true);

        var result = await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            GameId: gameId,
            ChampionName: "Ahri",
            Win: true,
            RequireReviewNotes: false,
            Snapshot: Snapshot(adherence)));

        Assert.True(result.Success);

        var entry = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.NotNull(entry);
        Assert.Equal(adherence, entry!.FocusAdherence);
    }

    [Fact]
    public async Task SaveAsync_NullFocusAdherence_PersistsAsUnanswered()
    {
        // Contract nuance: unlike improvement_note / mental_handled / the
        // attribution texts (which are null=unchanged), FocusAdherence is
        // null=unanswered. SaveAsync calls UpdateFocusAdherenceAsync
        // UNCONDITIONALLY, so a null snapshot value writes NULL — even over a
        // previously stamped answer. This is the documented design (the form
        // always sends the current adherence state). Assert that real behavior.
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        const long gameId = 7202;
        await scope.SeedGameAsync(gameId, champion: "Ahri", win: true);

        // Stamp "yes" first.
        var first = await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            gameId, "Ahri", true, false, Snapshot(2)));
        Assert.True(first.Success);
        Assert.Equal(2, (await scope.SessionLog.GetEntryAsync(gameId))!.FocusAdherence);

        // A save carrying null re-writes NULL (back to unanswered).
        var second = await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            gameId, "Ahri", true, false, Snapshot(null)));
        Assert.True(second.Success);

        var entry = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.NotNull(entry);
        Assert.Null(entry!.FocusAdherence);
    }

    [Fact]
    public async Task SaveAsync_ChangingFocusAdherence_Overwrites()
    {
        // A re-save with a different concrete answer must overwrite, not append
        // or ignore.
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        const long gameId = 7203;
        await scope.SeedGameAsync(gameId, champion: "Ahri", win: true);

        await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            gameId, "Ahri", true, false, Snapshot(0))); // no
        Assert.Equal(0, (await scope.SessionLog.GetEntryAsync(gameId))!.FocusAdherence);

        await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            gameId, "Ahri", true, false, Snapshot(2))); // changed to yes

        var entry = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.Equal(2, entry!.FocusAdherence);
    }

    [Fact]
    public async Task DeleteAsync_PreservesFocusAdherence()
    {
        // ClearReviewMarkersAsync (the delete-review path) is documented to NOT
        // touch focus_adherence so the live-computed adherence streak stays
        // byte-identical. Verify a saved adherence answer survives review delete.
        using var scope = new SidecarWriteScope();
        await scope.InitializeAsync();

        const long gameId = 7204;
        await scope.SeedGameAsync(gameId, champion: "Ahri", win: true);

        var save = await scope.ReviewWorkflow.SaveAsync(new SaveReviewRequest(
            gameId, "Ahri", true, false, Snapshot(2)));
        Assert.True(save.Success);
        Assert.Equal(2, (await scope.SessionLog.GetEntryAsync(gameId))!.FocusAdherence);

        var del = await scope.ReviewWorkflow.DeleteAsync(gameId);
        Assert.True(del.Success);

        var entry = await scope.SessionLog.GetEntryAsync(gameId);
        Assert.NotNull(entry); // delete keeps the behavioral row
        Assert.Equal(2, entry!.FocusAdherence); // adherence preserved
    }
}
