using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// v3.10: the "Auto-fill Timeline Inbox from game events" setting decides
/// whether the post-game pass's own anchors (objev:/objcrit:) show up as
/// review moments on the per-game surfaces. Anything the user touched stays
/// visible regardless; Patterns are unaffected (they read the repository).
/// </summary>
public sealed class EvidenceAutoAnchorsTests
{
    private static EvidenceItemRecord Row(
        string sourceKind = EvidenceKinds.TimelineRegion,
        string sourceKey = "objev:TRADE:120",
        string note = "",
        string status = EvidenceStatuses.Evidence,
        long? objectiveId = null,
        long? promptId = null,
        long? conceptTagId = null,
        long? matchupNoteId = null,
        string polarity = EvidencePolarities.Neutral) => new(
            Id: 1, GameId: 10, SourceKind: sourceKind, SourceId: null, SourceKey: sourceKey,
            StartTimeSeconds: 100, EndTimeSeconds: 140, Title: "Trade", Note: note,
            ObjectiveId: objectiveId, ObjectiveTitle: "", ConceptTagId: conceptTagId, ConceptTagName: "",
            MatchupNoteId: matchupNoteId, Polarity: polarity, Status: status,
            CreatedAt: 0, UpdatedAt: 0, ChampionName: "Ahri", Win: true, GameTimestamp: 0,
            PromptId: promptId);

    [Fact]
    public void UntouchedTrackedEventAndCriterionAnchors_AreAutoAnchors()
    {
        Assert.True(EvidenceAutoAnchors.IsUntouched(Row(sourceKey: "objev:TRADE:120")));
        Assert.True(EvidenceAutoAnchors.IsUntouched(Row(sourceKey: "objev:NUMBERS_UP_TEAMFIGHT:900")));
        // The stamped default polarity is still untouched: deaths / outnumbered
        // fights / failed criteria are written as bad.
        Assert.True(EvidenceAutoAnchors.IsUntouched(Row(sourceKey: "objev:DEATH:300", polarity: EvidencePolarities.Bad)));
        Assert.True(EvidenceAutoAnchors.IsUntouched(Row(sourceKey: "objev:OUTNUMBERED_TEAMFIGHT:1121-1137", polarity: EvidencePolarities.Bad)));
        Assert.True(EvidenceAutoAnchors.IsUntouched(Row(sourceKey: "objcrit:7", polarity: EvidencePolarities.Bad)));
    }

    [Fact]
    public void AGoodOrBadJudgement_IsATriageAction()
    {
        // The Review page's Good / Bad chips change only polarity — that must
        // count as the user having looked at the moment.
        Assert.False(EvidenceAutoAnchors.IsUntouched(Row(sourceKey: "objev:TRADE:120", polarity: EvidencePolarities.Bad)));
        Assert.False(EvidenceAutoAnchors.IsUntouched(Row(sourceKey: "objev:TRADE:120", polarity: EvidencePolarities.Good)));
        Assert.False(EvidenceAutoAnchors.IsUntouched(Row(sourceKey: "objev:DEATH:300", polarity: EvidencePolarities.Good)));
        Assert.False(EvidenceAutoAnchors.IsUntouched(Row(sourceKey: "objcrit:7", polarity: EvidencePolarities.Good)));
        Assert.False(EvidenceAutoAnchors.IsUntouched(Row(matchupNoteId: 2)));
    }

    [Fact]
    public void AnythingTheUserTouched_IsNotAnAutoAnchor()
    {
        Assert.False(EvidenceAutoAnchors.IsUntouched(Row(note: "walked up without vision")));
        Assert.False(EvidenceAutoAnchors.IsUntouched(Row(status: EvidenceStatuses.Highlight)));
        Assert.False(EvidenceAutoAnchors.IsUntouched(Row(status: EvidenceStatuses.NeedsReview)));
        Assert.False(EvidenceAutoAnchors.IsUntouched(Row(objectiveId: 3)));
        Assert.False(EvidenceAutoAnchors.IsUntouched(Row(promptId: 5)));
        Assert.False(EvidenceAutoAnchors.IsUntouched(Row(conceptTagId: 9)));
        // Promoted to a clip: source kind flips, key is rewritten.
        Assert.False(EvidenceAutoAnchors.IsUntouched(Row(sourceKind: EvidenceKinds.Clip, sourceKey: "clip:44")));
        // A hand-made timeline region is not the pass's row.
        Assert.False(EvidenceAutoAnchors.IsUntouched(Row(sourceKey: "region:12")));
    }

    [Fact]
    public void ForSurface_HidesOnlyUntouchedAutoAnchors_WhenTheSettingIsOff()
    {
        var rows = new[]
        {
            Row(sourceKey: "objev:TRADE:120"),
            Row(sourceKey: "objev:TRADE:300", note: "noted"),
            Row(sourceKind: EvidenceKinds.Clip, sourceKey: "clip:44"),
        };

        var on = EvidenceAutoAnchors.ForSurface(rows, autoFillEnabled: true);
        var off = EvidenceAutoAnchors.ForSurface(rows, autoFillEnabled: false);

        Assert.Equal(3, on.Count);
        Assert.Equal(2, off.Count);
        Assert.DoesNotContain(off, r => r.SourceKey == "objev:TRADE:120");
    }
}
