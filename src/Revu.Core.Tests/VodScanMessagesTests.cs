using Revu.Core.Services;

namespace Revu.Core.Tests;

/// <summary>
/// P-041: the exact strings the Settings "Scan for VODs" status line renders.
/// The sidecar endpoint delegates to VodScanMessages, so these ARE the wire
/// contract for scan feedback.
/// </summary>
public sealed class VodScanMessagesTests
{
    [Fact]
    public void Success_MatchedWins_EvenWithZeroLeftoverNote()
    {
        var text = VodScanMessages.Success(matched: 3, recordingCount: 186, unmatchedNote: "");

        Assert.Equal("Matched 3 VOD(s) to games! (186 recordings found)", text);
    }

    [Fact]
    public void Success_AppendsUnmatchedNote_AfterMatches()
    {
        var note = VodScanMessages.UnmatchedRecentNote(2);
        var text = VodScanMessages.Success(matched: 1, recordingCount: 10, unmatchedNote: note);

        Assert.Equal(
            "Matched 1 VOD(s) to games! (10 recordings found) 2 recording(s) from the last 7 days match no game.",
            text);
    }

    [Fact]
    public void Success_ZeroRecordings_PointsAtFolderSetup()
    {
        var text = VodScanMessages.Success(matched: 0, recordingCount: 0, unmatchedNote: "");

        Assert.Equal("No video files found. Check that your Ascent folder is set and contains recordings.", text);
    }

    [Fact]
    public void Success_RecordingsButNoMatches_ExplainsWhy()
    {
        var text = VodScanMessages.Success(matched: 0, recordingCount: 186, unmatchedNote: "");

        Assert.Equal(
            "Found 186 recordings but no new matches. Games may already be linked or outside the match window.",
            text);
    }

    [Fact]
    public void UnmatchedRecentNote_EmptyWhenZero()
    {
        Assert.Equal("", VodScanMessages.UnmatchedRecentNote(0));
        Assert.Equal(" 5 recording(s) from the last 7 days match no game.", VodScanMessages.UnmatchedRecentNote(5));
    }

    [Fact]
    public void DatabaseFailure_LeadsWithTheRecordingCount_SoTheUserKnowsTheirVodsAreFine()
    {
        var text = VodScanMessages.DatabaseFailure(186, "Revu's database file is missing (expected at X).");

        Assert.Equal(
            "Found 186 recording(s), but Revu couldn't open its database to link recordings to games: " +
            "Revu's database file is missing (expected at X).",
            text);
    }

    [Fact]
    public void DatabaseFailure_OmitsCountWhenNothingWasFound()
    {
        var text = VodScanMessages.DatabaseFailure(0, "reason.");

        Assert.Equal("Revu couldn't open its database to link recordings to games: reason.", text);
    }
}
